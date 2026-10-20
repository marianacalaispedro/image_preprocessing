using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using System.IO.Compression;
using Avalonia.Media;

namespace ImageApp
{
    public partial class MainWindow : Window
    {
        // Prewitt kernels -- shared by EdgeMagnitude and Task1
        private static readonly sbyte[,] PrewittHorizontal = { {-1, 0, 1}, {-1, 0, 1}, {-1, 0, 1} };
        private static readonly sbyte[,] PrewittVertical   = { {-1, -1, -1}, {0, 0, 0}, {1, 1, 1} };
        private WriteableBitmap? _loadedBitmap; // the raw loaded image, kept in color, for display in OriginalImage
        private byte[,,]? _loadedColorPixels; // [x, y, channel] with channel 0=R, 1=G, 2=B -- extracted once at load time
        private object? _processedResult; // Processed grayscale values (nullable)

        // Simple fixed defaults used by the functions below until you add your own
        // GUI controls (TextBoxes, ComboBoxes, etc.) to let the user set these values.
        private byte _threshold = 128;

        // Enum for operations. As you implement each function, add a case for it
        // in OnApply below; the dropdown is populated automatically from this list.
        //
        // NOTE: Task1, Task2, and Task3 (from the assignment text) are NOT listed here.
        // You need to add those dropdown entries yourself as part of implementing them.
        private enum ProcessingFunctions
        {
            ConvertToGrayscale,
            InvertImage,
            AdjustContrast,
            ConvolveImage,
            MedianFilter,
            EdgeMagnitude,
            ThresholdImage,
            BinaryErodeImage,
            BinaryDilateImage,
            BinaryOpenImage,
            BinaryCloseImage,
            GrayscaleErodeImage,
            GrayscaleDilateImage,
            Task1,
            Task2,
            Task3,
            HistogramEqualization,
            SequentialRegionLabeling,
            HoughTransform,
            HoughPeakFinding,
            HoughLineSegments,
            BinaryPipeline,
            GrayscalePipeline,
            FloodFill,
        }

        public MainWindow()
        {
            InitializeComponent();

            OperationBox.ItemsSource = Enum.GetValues<ProcessingFunctions>();
            OperationBox.SelectedIndex = 0; // Select first item by default
        }

        /// <summary>
        /// Opens a file picker dialog, loads the selected image, and extracts its color pixel data.
        /// </summary>
        private async void OnLoadImage(object? sender, RoutedEventArgs e)
        {
            if (!StorageProvider.CanOpen)
            {
                StatusText.Text = "Opening files is not supported on this system.";
                return;
            }

            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(
                    new()
                    {
                        Title = "Open Image",
                        AllowMultiple = false,
                        FileTypeFilter = [FilePickerFileTypes.ImageAll]
                    }
                );

                var file = files.FirstOrDefault();
                if (file == null)
                    return;

                await using var stream = await file.OpenReadAsync();

                // Decode the file using Avalonia's own image loader
                using var decoded = new Bitmap(stream);
                var size = decoded.PixelSize;
                int width = size.Width;
                int height = size.Height;

                // Force a known, fixed pixel layout (RGBA, 8 bits per channel, unpremultiplied alpha)
                // so we can reliably read raw bytes regardless of the source file's own format.
                _loadedBitmap?.Dispose();
                _loadedBitmap = new(size, new(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);

                _loadedColorPixels = new byte[width, height, 3];
                using (var fb = _loadedBitmap.Lock())
                {
                    decoded.CopyPixels(fb, AlphaFormat.Unpremul);
                    // ^ transcodes the decoded image into our WriteableBitmap's RGBA8888 layout

                    int totalBytes = fb.RowBytes * height;
                    byte[] buffer = new byte[totalBytes];
                    Marshal.Copy(fb.Address, buffer, 0, totalBytes);

                    for (int y = 0; y < height; y++)
                    {
                        int rowStart = y * fb.RowBytes;
                        for (int x = 0; x < width; x++)
                        {
                            int idx = rowStart + x * 4; // 4 bytes per pixel: R, G, B, A
                            _loadedColorPixels[x, y, 0] = buffer[idx + 0];
                            _loadedColorPixels[x, y, 1] = buffer[idx + 1];
                            _loadedColorPixels[x, y, 2] = buffer[idx + 2];
                        }
                    }
                }

                _processedResult = null;
                (OriginalImage.Source as IDisposable)?.Dispose();
                OriginalImage.Source = _loadedBitmap;
                (ProcessedImage.Source as IDisposable)?.Dispose();
                ProcessedImage.Source = null;
                StatusText.Text = $"Loaded image ({width} \u00d7 {height} px).";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to load image: {ex.Message}";
            }
        }

        /// <summary>
        /// Saves the current processed grayscale image to disk as a PNG file.
        /// </summary>
        private async void OnSaveImage(object? sender, RoutedEventArgs e)
        {
            if (_processedResult == null)
            {
                StatusText.Text = "No processed image to save. Apply an operation first.";
                return;
            }

            if (!StorageProvider.CanSave)
            {
                StatusText.Text = "Saving files is not supported on this system.";
                return;
            }

            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(
                    new()
                    {
                        Title = "Save Processed Image",
                        SuggestedFileName = "processed.png",
                        DefaultExtension = "png",
                        FileTypeChoices = [FilePickerFileTypes.ImagePng]
                    }
                );

                if (file == null)
                    return;

                using var bmp = _processedResult switch
                {
                    byte[,,] colorImg => ColorArrayToBitmap(colorImg),
                    byte[,] grayImg => ByteArrayToBitmap(grayImg),
                    _ => throw new NotSupportedException()
                };

                await using var stream = await file.OpenWriteAsync();
                bmp.Save(stream);
                StatusText.Text = "Processed image saved successfully.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to save image: {ex.Message}";
            }
        }

        /// <summary>
        /// Dispatches the selected image processing operation on a background task
        /// to keep the UI responsive during heavy computations.
        /// </summary>
        private async void OnApply(object? sender, RoutedEventArgs e)
        {
            if (_loadedColorPixels == null)
            {
                StatusText.Text = "Please load an image first.";
                return;
            }

            if (OperationBox.SelectedItem is not ProcessingFunctions selected)
            {
                StatusText.Text = "Please select a valid operation.";
                return;
            }

            
            string selectedFilter = (FilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            
            if (!byte.TryParse(ThresholdBox.Text, out byte threshold))
            {
                StatusText.Text = "Threshold must be an integer 0–255.";
                return;
            }

            if (!byte.TryParse(KernelSizeBox.Text, out byte kernelSize) || kernelSize < 1 || kernelSize % 2 == 0)
            {
                StatusText.Text = "Kernel size must be a positive odd integer.";
                return;
            }

            if (!float.TryParse(SigmaBox.Text, out float sigma) || sigma <= 0)
            {
                StatusText.Text = "Sigma must be a positive number.";
                return;
            }

            if (!byte.TryParse(StructElemSizeBox.Text, out byte structElemSize) || structElemSize < 1 || structElemSize % 2 == 0)
            {
                StatusText.Text = "StructElem size must be a positive odd integer.";
                return;
            }

            if (!byte.TryParse(ThreshMagBox.Text, out byte t_mag))
            {
                StatusText.Text = "t_mag must be an integer between 0-255.";
                return;
            }

            if (!int.TryParse(ThreshPeakBox.Text, out int t_peak))
            {
                StatusText.Text = "t_peak must be an integer between 0-255.";
                return;
            }

            if (!byte.TryParse(MinThreshBox.Text, out byte minThresh))
            {
                StatusText.Text = "Minimum intensity must be an integer between 0-255.";
                return;
            }

            if (!byte.TryParse(MinSegLenBox.Text, out byte minSegLen))
            {
                StatusText.Text = "t_mag must be an integer.";
                return;
            }

            if (!byte.TryParse(MaxGapBox.Text, out byte maxGap))
            {
                StatusText.Text = "t_mag must be an integer.";
                return;
            }


            ApplyButton.IsEnabled = false;
            StatusText.Text = "Processing...";

            byte[,,] colorPixels = _loadedColorPixels;

            try
            {
                // Run computation and bitmap generation on a background task
                // to keep the UI dispatcher thread responsive during heavy operations.
                var (resultGray, resultBmp) = await Task.Run(() =>
                {
                    // Grayscale conversion happens here on every Apply so every
                    // operation always starts from the original loaded image, never chained
                    // from a previous Apply's result.
                    byte[,] gray = ConvertToGrayscale(colorPixels);
                    object result = gray;

                    bool[,] generalStructElem = new bool[structElemSize, structElemSize];
                    for (int x = 0; x < structElemSize; x++)
                    for (int y = 0; y < structElemSize; y++)
                    {
                        generalStructElem[x, y] = true;                            
                    }

                    int?[,] structElemGrey =
                    {
                        {null, 0, 2, 0, null},
                           {0, 2, 3, 2, 0},
                           {2, 3, 5, 3, 2},
                           {0, 2, 3, 2, 0},
                        {null, 0, 2, 0, null}
                    };

                    switch (selected)
                    {
                        case ProcessingFunctions.ConvertToGrayscale:
                            // Already fully working; gray already holds the grayscale
                            // conversion result at this point (computed above, before this switch),
                            // so nothing further is needed here.
                            break;
                        case ProcessingFunctions.InvertImage:
                            result = InvertImage(gray);
                            break;
                        case ProcessingFunctions.AdjustContrast:
                            result = AdjustContrast(gray);
                            break;
                        case ProcessingFunctions.ConvolveImage:
                            result = ConvolveImage(gray, CreateGaussianFilter(5, 1.0f));
                            break;
                        case ProcessingFunctions.MedianFilter:
                            result = MedianFilter(gray, 5);
                            break;
                        case ProcessingFunctions.EdgeMagnitude:
                        {
                            result = EdgeMagnitude(gray, PrewittHorizontal, PrewittVertical);
                            break;
                        }
                        case ProcessingFunctions.ThresholdImage:
                            result = ThresholdImage(gray, _threshold);
                            break;

                        case ProcessingFunctions.BinaryErodeImage:
                        {
                            bool[,] structElem = generalStructElem; // Define this structuring element yourself
                            result = BinaryErodeImage(gray, structElem);
                            break;
                        }

                        case ProcessingFunctions.BinaryDilateImage:
                        {
                            bool[,] structElem = generalStructElem; // Define this structuring element yourself
                            result = BinaryDilateImage(gray, structElem);
                            break;
                        }

                        case ProcessingFunctions.BinaryOpenImage:
                        {
                            bool[,] structElem = generalStructElem; // Define this structuring element yourself
                            result = BinaryOpenImage(gray, structElem);
                            break;
                        }

                        case ProcessingFunctions.BinaryCloseImage:
                        {
                            bool[,] structElem = generalStructElem; // Define this structuring element yourself
                            result = BinaryCloseImage(gray, structElem);
                            break;
                        }

                        case ProcessingFunctions.GrayscaleErodeImage:
                        {
                            int?[,] grayStructElem = structElemGrey; // Define this structuring element yourself
                            result = GrayscaleErodeImage(gray, grayStructElem);
                            break;
                        }

                        case ProcessingFunctions.GrayscaleDilateImage:
                        {
                            int?[,] grayStructElem = structElemGrey; // Define this structuring element yourself
                            result = GrayscaleDilateImage(gray, grayStructElem);
                            break;
                        }

                        case ProcessingFunctions.Task1:
                        {
                            result = Task1(gray, selectedFilter, kernelSize, sigma, threshold);
                            break;
                        }

                        case ProcessingFunctions.Task2:
                        {
                            result = Task2(gray, structElemSize);   
                            break;     
                        }

                        case ProcessingFunctions.Task3:
                        {
                            result = Task3(gray, structElemSize);
                            break;        
                        }

                        case ProcessingFunctions.HistogramEqualization:
                        {
                            result = HistogramEqualization(gray);
                            break;
                        }

                        case ProcessingFunctions.SequentialRegionLabeling:
                        {
                            result = SequentialRegionLabeling(gray);
                            break;
                        }

                        case ProcessingFunctions.HoughTransform:
                        {
                            byte[,] temp = AccumulatorToByte(HoughTransform(gray));
                            //result = AdjustContrast(temp);
                            result = temp;
                            break;
                        }
                        
                        case ProcessingFunctions.HoughPeakFinding:
                            var (peaks, viz) = HoughPeakFinding(HoughTransform(gray),  t_peak);
                            result = viz;
                            break;

                        case ProcessingFunctions.HoughLineSegments:
                        {
                            List<(int, int)> listOfPeaks = [(50, 50), (0, 0), (20, 20)];
                            int minIntensityThreshold = 50;
                            int minSegmentLength = 0;
                            int maxGapParameter = 0;
                            (byte[,,] output, List<((int, int), (int, int))> lines) = HoughLineSegments(gray, listOfPeaks, minIntensityThreshold, minSegmentLength, maxGapParameter);
                            result = output;
                            break;  
                        }

                        case ProcessingFunctions.BinaryPipeline:
                        {
                            result = BinaryPipeline(gray, t_mag, t_peak, minThresh, minSegLen, maxGap);
                            break;
                        }

                        case ProcessingFunctions.GrayscalePipeline:
                        {
                            result = GrayscalePipeline(gray, t_peak, minThresh, minSegLen, maxGap);
                            break; 
                        }

                        case ProcessingFunctions.FloodFill:
                            result = FloodFill(gray);
                            break;
                        
                        default:
                            throw new NotSupportedException($"Operation '{selected}' is not implemented in the OnApply switch.");
                    }

                    var bmp = result switch 
                    {
                        byte[,,] colorImg => ColorArrayToBitmap(colorImg),
                        byte[,] grayImg => ByteArrayToBitmap(grayImg),
                        _ => throw new NotSupportedException()
                    };
                    return (result, bmp);
                });

                _processedResult = resultGray;
                (ProcessedImage.Source as IDisposable)?.Dispose();
                ProcessedImage.Source = resultBmp;
                StatusText.Text = $"Completed {selected}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error applying {selected}: {ex.Message}";
            }
            finally
            {
                ApplyButton.IsEnabled = true;
            }
        }

        // ====================================================================
        // ==================== GIVEN (already implemented) ==================
        // ====================================================================

        /// <summary>
        /// Converts loaded color pixel data (<c>[x, y, channel]</c> where channel 0=R, 1=G, 2=B)
        /// to single-channel grayscale by averaging RGB values.
        /// </summary>
        /// <param name="colorPixels">The 3D array of color pixels extracted at load time.</param>
        /// <returns>A 2D array of grayscale byte intensities with values in [0, 255].</returns>
        private static byte[,] ConvertToGrayscale(byte[,,] colorPixels)
        {
            int w = colorPixels.GetLength(0);
            int h = colorPixels.GetLength(1);
            byte[,] gray = new byte[w, h];
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                int r = colorPixels[x, y, 0];
                int g = colorPixels[x, y, 1];
                int b = colorPixels[x, y, 2];
                gray[x, y] = (byte)((r + g + b) / 3);
            }

            return gray;
        }

        // ====================================================================
        // ==================== FUNCTIONS TO IMPLEMENT =======================
        // ====================================================================

        // general point operation func to reduce for loop usage
        private byte[,] applyPointOperationToAll(byte[,] inputImage, Func<byte, byte> pointOperation)
        {
            for (int x = 0; x < inputImage.GetLength(0); x++)
            for (int y = 0; y < inputImage.GetLength(1); y++) 
            {
                inputImage[x, y] = pointOperation(inputImage[x,y]);
            }
            return inputImage;
        }

        /// <summary>
        /// Inverts the intensity values of the input grayscale image.
        /// </summary>
        /// <param name="inputImage">The 2D input grayscale image.</param>
        /// <returns>A new 2D grayscale image with inverted intensities.</returns>
        private byte[,] InvertImage(byte[,] inputImage)
        {
            // create temporary grayscale image
            byte[,] tempImage = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];
            
            // TODO: add your functionality and checks
            // for (int x = 0; x < tempImage.GetLength(0); x++)
            // for (int y = 0; y < tempImage.GetLength(1); y++)
            // {
            //     tempImage[x, y] = (byte)(255 - inputImage[x, y]);
            // }

            // return tempImage;
            Func<byte, byte> inverse = c => (byte)(255 - c);
            inputImage = applyPointOperationToAll(inputImage, inverse);
            return inputImage;
        }

        /// <summary>
        /// Adjusts the contrast of the input grayscale image.
        /// </summary>
        /// <param name="inputImage">The 2D input grayscale image.</param>
        /// <returns>A new 2D grayscale image with adjusted contrast.</returns>
        private byte[,] AdjustContrast(byte[,] inputImage)
        {
            // create temporary grayscale image
            byte[,] tempImage = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];
            byte min = 255;
            byte max = 0;
        
            // TODO: add your functionality and checks
            for (int x = 0; x < inputImage.GetLength(0); x++)
            for (int y = 0; y < inputImage.GetLength(1); y++)
            {
                if (inputImage[x, y] < min) min = inputImage[x, y];
                if (inputImage[x, y] > max) max = inputImage[x, y];  
            }

            // for (int x = 0; x < tempImage.GetLength(0); x++)
            // for (int y = 0; y < tempImage.GetLength(1); y++)
            // {
            //     tempImage[x, y] = (byte)((inputImage[x, y]-min) * 255/(max-min));  
            // }
            Func<byte, byte> adjust = c => (byte)((c-min) * 255/(max-min));
            tempImage = applyPointOperationToAll(inputImage, adjust);

            return tempImage;
        }

        /// <summary>
        /// Generates a normalized 2D Gaussian filter kernel of the specified size and standard deviation.
        /// </summary>
        /// <param name="size">Kernel dimension (odd integer, e.g. 3, 5, 7).</param>
        /// <param name="sigma">Gaussian standard deviation parameter.</param>
        /// <returns>A 2D float array representing the normalized filter kernel.</returns>
        private float[,] CreateGaussianFilter(byte size, float sigma)
        {
            // create the filter
            float[,] filter = new float[size, size];
            float sum = 0;

            // TODO: add your functionality and checks
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    int x = i - size / 2;
                    int y = j - size / 2;
                    filter[i, j] = (float)Math.Exp(-(x * x + y * y) / (2 * sigma * sigma));
                    sum += filter[i, j];
                }
            }
            // normalizing the filter
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    filter[i, j] /= sum;
                }
            }

            return filter;
        }

        private int?[,] CreateGreyStructureElement(byte size)
        {
            int?[,] greyStructElem = new int?[size, size];

            for (int x = 0; x < size; x++)
            for (int y = 0; y < size; y++)
            {
                greyStructElem[x, y] = 1;        
            }

            return greyStructElem;
        }

        /// <summary>
        /// Convolves a grayscale image with a given 2D filter kernel.
        /// </summary>
        /// <param name="inputImage">The 2D input grayscale image.</param>
        /// <param name="filter">The 2D filter kernel to apply.</param>
        /// <returns>The convolved grayscale image.</returns>
        private byte[,] ConvolveImage(byte[,] inputImage, float[,] filter)
        {
            // create temporary grayscale image
            byte[,] tempImage = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];

            int size = filter.GetLength(0);
            int padding = size / 2;
            
            // creating padding
            int newWidth = inputImage.GetLength(0) + 2 * padding;
            int newHeight = inputImage.GetLength(1) + 2 * padding;

            // creating temporary padded imag
            byte[,] paddedImage = new byte[newWidth, newHeight];

            for (int x = 0; x < newWidth; x++)
            for (int y = 0; y < newHeight; y++)
            {
                int originalX = x - padding;
                int originalY = y - padding;

                originalX = Math.Max(0, Math.Min(originalX, inputImage.GetLength(0) - 1));
                originalY = Math.Max(0, Math.Min(originalY, inputImage.GetLength(1) - 1));

                paddedImage[x, y] = inputImage[originalX, originalY];
            }

            // TODO: add your functionality and checks, think about border handling and type conversion
            for (int x = 0; x < inputImage.GetLength(0); x++)
            {
            for (int y = 0; y < inputImage.GetLength(1); y++)
            {
                float sum = 0;
                for (int i = 0; i < size ; i++)
                {
                    for (int j = 0; j < size; j++)
                        {
                            sum += filter[i, j] * paddedImage[x + i, y + j];
                        }
                }
                sum = Math.Max(0, Math.Min(255, sum));
                tempImage[x, y] = (byte)sum;  
            }
            }
            return tempImage;
        }

        /// <summary>
        /// Applies a median filter of the given kernel size to reduce noise.
        /// </summary>
        /// <param name="inputImage">The 2D input grayscale image.</param>
        /// <param name="kernelSize">The size of the local neighborhood window (odd integer).</param>
        /// <returns>The filtered grayscale image.</returns>
        private byte[,] MedianFilter(byte[,] inputImage, byte kernelSize)
        {
            // create temporary grayscale image
            byte[,] tempImage = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];
            
            int padding = kernelSize / 2;
            
            // creating padding
            int newWidth = inputImage.GetLength(0) + 2 * padding;
            int newHeight = inputImage.GetLength(1) + 2 * padding;

            // creating temporary padded imag
            byte[,] paddedImage = new byte[newWidth, newHeight];

            for (int x = 0; x <newWidth; x++)
            for (int y = 0; y < newHeight; y++)
            {
                int originalX = x - padding;
                int originalY = y - padding;

                originalX = Math.Max(0, Math.Min(originalX, inputImage.GetLength(0) - 1));
                originalY = Math.Max(0, Math.Min(originalY, inputImage.GetLength(1) - 1));

                paddedImage[x, y] = inputImage[originalX, originalY];
            }

            List<byte> window = new List<byte>(kernelSize * kernelSize);
            // TODO: add your functionality and checks, think about border handling
            for (int x = 0; x < inputImage.GetLength(0); x++)
            {
                for (int y = 0; y < inputImage.GetLength(1); y++)
                {
                    window.Clear();
                    for (int i = 0; i < kernelSize ; i++)
                    {
                        for (int j = 0; j < kernelSize; j++)
                            {
                                window.Add(paddedImage[x + i,y + j]);
                            }
                    }
                window.Sort();
                tempImage[x, y] = window[window.Count/2];  
                }
            }
            return tempImage;
        }

        /// <summary>
        /// Computes edge magnitude from horizontal and vertical derivative kernels.
        /// </summary>
        /// <param name="inputImage">The 2D input grayscale image.</param>
        /// <param name="horizontalKernel">Horizontal gradient kernel.</param>
        /// <param name="verticalKernel">Vertical gradient kernel.</param>
        /// <returns>The edge gradient magnitude image.</returns>
        private byte[,] EdgeMagnitude(
            byte[,] inputImage,
            sbyte[,] horizontalKernel,
            sbyte[,] verticalKernel
        )
        {
            // create temporary grayscale image
            byte[,] tempImage = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];

            
            List<sbyte[,]> kernels = new List<sbyte[,]>{horizontalKernel, verticalKernel};

            int padding = horizontalKernel.GetLength(0) / 2;
            
            // creating padding
            int newWidth = inputImage.GetLength(0) + 2 * padding;
            int newHeight = inputImage.GetLength(1) + 2 * padding;

            // creating temporary padded imag
            byte[,] paddedImage = new byte[newWidth, newHeight];

            for (int x = 0; x < newWidth; x++)
            for (int y = 0; y < newHeight; y++)
            {
                int originalX = x - padding;
                int originalY = y - padding;

                originalX = Math.Max(0, Math.Min(originalX, inputImage.GetLength(0) - 1));
                originalY = Math.Max(0, Math.Min(originalY, inputImage.GetLength(1) - 1));

                paddedImage[x, y] = inputImage[originalX, originalY];
            }

            for (int x = 0; x < inputImage.GetLength(0); x++)
            {
            for (int y = 0; y < inputImage.GetLength(1); y++)
            {
                List<float> derivatives = new List<float>{};
                foreach (sbyte[,]kernel in kernels)
                {
                    float sum = 0;
                    for (int i = 0; i < kernel.GetLength(0) ; i++)
                    {
                        for (int j = 0; j < kernel.GetLength(1); j++)
                            {
                                sum += kernel[i, j] * paddedImage[x + i, y + j];
                            }
                    }
                    derivatives.Add(sum);
                     
                }
                double magnitude = Math.Sqrt(Math.Pow(derivatives[0], 2) + Math.Pow(derivatives[1], 2));
                magnitude = Math.Max(0, Math.Min(255, magnitude));
                tempImage[x, y] = (byte)magnitude;
            }
            }
            return tempImage;
        }

        /// <summary>
        /// Thresholds a grayscale image into a binary representation based on a cutoff value.
        /// </summary>
        /// <param name="inputImage">The 2D input grayscale image.</param>
        /// <param name="threshold">Intensity threshold cutoff value in [0, 255].</param>
        /// <returns>A binary image represented as byte intensities (e.g. 0 and 255).</returns>
        private byte[,] ThresholdImage(byte[,] inputImage, byte threshold)
        {
            // create temporary grayscale image
            byte[,] tempImage = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];

            for (int x = 0; x < inputImage.GetLength(0); x++)
            for (int y = 0; y < inputImage.GetLength(1); y++)
            {
                if (inputImage[x, y] >= threshold)
                    tempImage[x, y] = 255;
                else
                    tempImage[x, y] = 0;
            }
            return tempImage;
        }

        /// <summary>
        /// Performs morphological binary erosion using the provided structuring element.
        /// </summary>
        /// <param name="inputImage">The binary input image.</param>
        /// <param name="structElem">2D boolean structuring element (true = foreground).</param>
        /// <returns>The eroded binary image.</returns>
        private byte[,] BinaryErodeImage(byte[,] inputImage, bool[,] structElem)
        {
            byte[,] output = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];

            // invert -> reflect structElem -> apply dilation -> invert
            inputImage = InvertImage(inputImage);
            structElem = ReflectStructElem(structElem);
            output = InvertImage(BinaryDilateImage(inputImage, structElem));

            return output;
        }

        // reflect structure element on x- AND y-axis
        private bool[,] ReflectStructElem(bool[,] structElem)
        {
            int structElemSize = structElem.GetLength(0);
            bool[,] output = new bool[structElemSize, structElemSize];

            for (int x = 0; x < structElemSize; x++)
            for (int y = 0; y < structElemSize; y++)
            {
                output[structElemSize - 1 - x, structElemSize - 1 - y] = structElem[x, y];
            }

            return output;
        } 

        /// <summary>
        /// Performs morphological binary dilation using the provided structuring element.
        /// </summary>
        /// <param name="inputImage">The binary input image.</param>
        /// <param name="structElem">2D boolean structuring element (true = foreground).</param>
        /// <returns>The dilated binary image.</returns>
        private byte[,] BinaryDilateImage(byte[,] inputImage, bool[,] structElem)
        {
            byte[,] output = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];

            int structElemSize = structElem.GetLength(0)/2;

            for (int x = 0; x < inputImage.GetLength(0); x++)
            for (int y = 0; y < inputImage.GetLength(1); y++)
            {    
                output[x, y] = BinaryDilate(x, y);
            }

            // singular application of structElem
            byte BinaryDilate(int x, int y)
            {
                for (int i = -1*structElemSize; i <= structElemSize; i++)
                for (int j = -1*structElemSize; j <= structElemSize; j++)
                {
                   if (!OutOfBounds(x+i, y+j, inputImage.GetLength(0), inputImage.GetLength(1)))
                   if (inputImage[x+i, y+j] == 255 && structElem[i+structElemSize, j+structElemSize]) return 255;
                }      
                return 0;
            }

            return output;
        }


        /// <summary>
        /// Performs morphological binary opening (erosion followed by dilation).
        /// </summary>
        /// <param name="inputImage">The binary input image.</param>
        /// <param name="structElem">2D boolean structuring element (true = foreground).</param>
        /// <returns>The opened binary image.</returns>
        private byte[,] BinaryOpenImage(byte[,] inputImage, bool[,] structElem)
        {
            byte[,] output = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];
            output = BinaryDilateImage(BinaryErodeImage(inputImage, structElem), structElem);

            return output;
        }

        /// <summary>
        /// Performs morphological binary closing (dilation followed by erosion).
        /// </summary>
        /// <param name="inputImage">The binary input image.</param>
        /// <param name="structElem">2D boolean structuring element (true = foreground).</param>
        /// <returns>The closed binary image.</returns>
        private byte[,] BinaryCloseImage(byte[,] inputImage, bool[,] structElem)
        {
            byte[,] output = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];
            output = BinaryErodeImage(BinaryDilateImage(inputImage, structElem), structElem);

            return output;
        }

        /// <summary>
        /// Performs morphological grayscale erosion using the provided structuring element.
        /// </summary>
        /// <param name="inputImage">The 2D grayscale input image.</param>
        /// <param name="structElem">2D integer structuring element defining neighborhood offsets.</param>
        /// <returns>The eroded grayscale image.</returns>
        private byte[,] GrayscaleErodeImage(byte[,] inputImage, int?[,] structElem)
        {
            byte[,] output = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];
            
                        int structElemSize = structElem.GetLength(0)/2;

            for (int x = 0; x < inputImage.GetLength(0); x++)
            for (int y = 0; y < inputImage.GetLength(1); y++)
            {
                int min = 255;
                for (int i = -1*structElemSize; i < structElemSize; i++)
                for (int j = -1*structElemSize; j < structElemSize; j++)
                {
                    // check if structElem cell falls out of bound or should not be checked (is null)
                    if (!OutOfBounds(x+i, y+j, inputImage.GetLength(0), inputImage.GetLength(1)) &&
                        structElem[i+structElemSize, j+structElemSize] != null)
                    {
                        int temp = (int)inputImage[x+i, y+j] - (int)structElem[i+structElemSize, j+structElemSize];
                        min = Math.Min(temp, min);
                    }                                    
                }

                output[x, y] = (byte)Math.Clamp(min, 0, 255);
            }

            return output;
        }

        /// <summary>
        /// Performs morphological grayscale dilation using the provided structuring element.
        /// </summary>
        /// <param name="inputImage">The 2D grayscale input image.</param>
        /// <param name="structElem">2D integer structuring element defining neighborhood offsets.</param>
        /// <returns>The dilated grayscale image.</returns>
        private byte[,] GrayscaleDilateImage(byte[,] inputImage, int?[,] structElem)
        {
            byte[,] output = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];

            int structElemSize = structElem.GetLength(0)/2;

            for (int x = 0; x < inputImage.GetLength(0); x++)
            for (int y = 0; y < inputImage.GetLength(1); y++)
            {
                int max = 0;
                for (int i = -1*structElemSize; i < structElemSize; i++)
                for (int j = -1*structElemSize; j < structElemSize; j++)
                {
                    // check if structElem cell falls out of bound or should not be checked (is null)
                    if (!OutOfBounds(x+i, y+j, inputImage.GetLength(0), inputImage.GetLength(1)) &&
                        structElem[i+structElemSize, j+structElemSize] != null)
                    {
                        int temp = (int)inputImage[x+i, y+j] + (int)structElem[i+structElemSize, j+structElemSize];
                        max = Math.Max(temp, max);
                    }                                    
                }

                output[x, y] = (byte)Math.Clamp(max, 0, 255);
            }

            return output;
        }

        private bool OutOfBounds(int x, int y, int xMax, int yMax)
        {
            return (x<0 || x>=xMax || y<0 || y>=yMax);
        }

        /// <summary>
        /// Performs Task 1.
        /// apply the selected filter (Gaussian or Median) with the given kernel size (and
        /// sigma, if Gaussian), then
        /// o apply edge detection, and finally
        /// o apply the given threshold to the result.
        /// o The output binary image should be shown in the GUI.
        /// </summary>
        /// <param name="inputImage">The 2D grayscale input image.</param>
        /// <param name="selectedFilter">The selected filter chosen by the user (Gaussian or Median).</param>
        /// <param name="kernelSize">The selected kernel size</param>
        /// <param name="sigma">The selected sigma value</param>
        /// <param name="threshold">The selected threshold
        /// <returns>The dilated grayscale image.</returns>
        
        private byte[,] Task1(byte[,] inputImage,string selectedFilter, byte kernelSize, float sigma, byte threshold)
        {
            byte[,] output = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];
            if (selectedFilter == "Gaussian")
            {
                float[,] kernel = CreateGaussianFilter(kernelSize, sigma);
                output = ConvolveImage(inputImage, kernel);
            }

            else // "Median"
            {
                output = MedianFilter(inputImage, (byte)kernelSize);
            }

            output = EdgeMagnitude(output, PrewittHorizontal, PrewittVertical);
            
            output = ThresholdImage(output, threshold);
            
            return output;
        }

        private byte[,] Task2(byte[,] inputImage, byte structElemSize)
        {
            byte[,] output = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];

            // grayscale erode
            int?[,] structElem = CreateGreyStructureElement(structElemSize);

            output = GrayscaleErodeImage(inputImage, structElem);

            // get number of distinct intensity value
            int nIntensity = 0;
            for (int intensity = 0; intensity < 256; intensity++)
            {
                nIntensity += containsIntensityValue(intensity) ? 1 : 0;
            }

            // get average pixel intensity value
            int sum = 0;
            for (int x = 0; x < inputImage.GetLength(0); x++)
            for (int y = 0; y < inputImage.GetLength(1); y++)
            {
                sum += output[x, y];
            }
            float avg = (float)sum / (inputImage.GetLength(0)*inputImage.GetLength(1));

            Console.WriteLine($"Number of greyscale values: {nIntensity}; Average intensity: {Math.Round(avg, 3)}");
            return output;

            // loop over image until given intensity value is found
            bool containsIntensityValue(int intensity)
            {
                for (int x = 0; x < inputImage.GetLength(0); x++)
                for (int y = 0; y < inputImage.GetLength(1); y++)
                {
                    if (output[x, y] == intensity) return true;        
                }
                return false;
            }
        }

        private byte[,] Task3(byte[,] inputImage, byte structElemSize)
        {
            byte[,] output = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];

            bool[,] structElem = new bool[structElemSize, structElemSize];
            for (int x = 0; x < structElemSize; x++)
            for (int y = 0; y < structElemSize; y++)
            {
                structElem[x, y] = true;                            
            }

            output = BinaryCloseImage(inputImage, structElem);

            int nForeground = 0;
            for (int x = 0; x < output.GetLength(0); x++)
            for (int y = 0; y < output.GetLength(1); y++)
            {
                nForeground += (output[x,y] == 255) ? 1 : 0; 
            }

            Console.WriteLine($"Number of foreground: {nForeground}");

            return output;
        }

        /// <summary>
        /// Makes the cumilitive histogram uniform. 
        /// </summary>
        /// <param name="inputImage">The 2D input grayscale image.</param>
        /// <returns>A binary image whit its comulative histogram equalized.</returns>
        private byte[,] HistogramEqualization(byte[,] inputImage)
        {
            // create temporary grayscale image
            byte[,] tempImage = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];
            
            // creating the histogram
            int[] histogram = new int[256];
            for (int x = 0; x < inputImage.GetLength(0); x++)
            for (int y = 0; y < inputImage.GetLength(1); y++)
            {
                histogram[inputImage[x, y]]++; 
            }

            int [] cumulativeHistogram = new int[256];
            int running = 0;
            for (int val = 0; val < histogram.Length; val++)
            {
                running += histogram[val];
                cumulativeHistogram[val] = running;
            }

            byte [] equalizedHistogram = new byte[256];
            for (int val = 0; val < cumulativeHistogram.Length; val++)
            {
                byte equalizedVal = (byte) (cumulativeHistogram[val] * (256 - 1)/ cumulativeHistogram[255]);
                if (equalizedVal > 255) equalizedVal = 255;
                equalizedHistogram[val] = equalizedVal;
            }

            for (int x = 0; x < inputImage.GetLength(0); x++)
            for (int y = 0; y < inputImage.GetLength(1); y++)
            {
                tempImage[x, y] = equalizedHistogram[inputImage[x,y]];
            }

            return tempImage;
        }

        private byte[,] SequentialRegionLabeling(byte[,] inputImage)
        {
            const byte pForeground = 255; 

            int w = inputImage.GetLength(0);
            int h = inputImage.GetLength(1);

            int currentLastLabel = 2;
            int[,] labeledImage = new int[w, h];

        
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if (inputImage[x, y] != pForeground) continue;

                var possibleLabels = new List<int>();

                for (int xn = x - 1; xn <= x + 1; xn++)
                for (int yn = y - 1; yn <= y + 1; yn++)
                {
                    if (xn == x && yn == y) continue;                         
                    if (xn < 0 || yn < 0 || xn >= w || yn >= h) continue;     
                    if (labeledImage[xn, yn] >= 2)
                        possibleLabels.Add(labeledImage[xn, yn]);
                }

                if (possibleLabels.Count > 0)
                {
                    int minLabel = possibleLabels[0];
                    for (int k = 1; k < possibleLabels.Count; k++)
                        if (possibleLabels[k] < minLabel) minLabel = possibleLabels[k];
                    labeledImage[x, y] = minLabel;
                }
                else
                {
                    currentLastLabel++;
                    labeledImage[x, y] = currentLastLabel;
                }
            }

            // resolving label collisions 
            int labelCount = currentLastLabel - 1;
            if (labelCount > 0)
            {
                int[,] labelConnectionsCount = new int[labelCount, labelCount];

                int Idx(int label) => label - 2;   

                for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    if (labeledImage[x, y] < 2) continue;

                    for (int xn = x - 1; xn <= x + 1; xn++)
                    for (int yn = y - 1; yn <= y + 1; yn++)
                    {
                        if (xn == x && yn == y) continue;
                        if (xn < 0 || yn < 0 || xn >= w || yn >= h) continue;

                        int a = labeledImage[xn, yn];
                        int b = labeledImage[x, y];
                        if (a >= 2 && a != b)
                            labelConnectionsCount[Idx(a), Idx(b)]++;
                    }
                }

                
                int[,] condensed = new int[labelCount, labelCount];
                for (int i = 0; i < labelCount; i++)
                for (int j = 0; j < labelCount; j++)
                    condensed[i, j] = labelConnectionsCount[i, j] + labelConnectionsCount[j, i];

                for (int l1 = labelCount - 1; l1 >= 1; l1--)
                for (int l2 = l1 - 1; l2 >= 0; l2--)
                {
                    if (condensed[l1, l2] <= 0) continue;

                    int highLabel = l1 + 2;
                    int lowLabel  = l2 + 2;

                    for (int x = 0; x < w; x++)
                    for (int y = 0; y < h; y++)
                        if (labeledImage[x, y] == highLabel)
                            labeledImage[x, y] = lowLabel;
                }
            }
            
            byte[,] labelImage = new byte[w, h];
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                labelImage[x, y] = (byte)labeledImage[x, y];   // 0 stays 0, labels 2.. are kept

            return AdjustContrast(labelImage);
        }

        private int[,] HoughTransform(byte[,] inputImage)
        {
            int thetaStepSize = 1;
            int rLoD = 1;
            
            int offsetX = inputImage.GetLength(0)/2;
            int offsetY = inputImage.GetLength(1)/2;

            bool isBinary = true;
            for (int x = 0; x < inputImage.GetLength(0); x++)
            for (int y = 0; y < inputImage.GetLength(1); y++)
            {
                if (inputImage[x, y] != 0 && inputImage[x, y] != 255) { isBinary = false; break; }          
            }

            int maxR = inputImage.GetLength(0) - 1 - offsetX + inputImage.GetLength(1) - 1 - offsetY;
            int[,] accumArr = new int[360/thetaStepSize, 2*maxR];

            Parallel.For(0, inputImage.GetLength(0), (x) => {
            Parallel.For(0, inputImage.GetLength(1), (y) =>
            {
                if (inputImage[x,y] > 0) 
                {    
                    for (float theta = 0; theta < 360/thetaStepSize; theta += thetaStepSize)
                    {
                        float rF = ((x - offsetX) * MathF.Cos(theta / 180.0f * MathF.PI)) + ((y - offsetY) * MathF.Sin(theta / 180.0f * MathF.PI));
                        int r = (int)MathF.Round(rF * rLoD) / rLoD;
                        // accumArr[(int)theta, r + maxR]++;
                        if (isBinary) Interlocked.Increment(ref accumArr[(int)theta, r + maxR]);
                        // accumArr[(int)theta, r + maxR] += inputImage[x, y];
                        else Interlocked.Add(ref accumArr[(int)theta, r + maxR], inputImage[x, y]);
                    }
                }        
            });
        });

            return accumArr;
        }

        private byte[,] AccumulatorToByte(int[,] acc)
        {
            int max = 1;
            foreach (int v in acc) if (v > max) max = v;
            byte[,] o = new byte[acc.GetLength(0), acc.GetLength(1)];
            for (int i = 0; i < acc.GetLength(0); i++)
            for (int j = 0; j < acc.GetLength(1); j++)
                o[i, j] = (byte)(acc[i, j] * 255L / max);
            return o;
        }

        private (List<(int r ,int theta)> peaks, byte[,] Visualization) HoughPeakFinding(int[,] accumArr, int tPeak)
        {
            int w = accumArr.GetLength(0);
            int h = accumArr.GetLength(1);
            
            int[,] accumArrMax = new int[w, h];

            // Using option B from the slides (Non-maximum suppression)
            for (int xaccumArr = 0; xaccumArr < w; xaccumArr ++)
            for (int yaccumArr = 0; yaccumArr < h; yaccumArr++)
            {    
                bool centerIsMax = true;
                for (int nx = xaccumArr - 1; nx <= xaccumArr + 1 && centerIsMax; nx++)
                for (int ny = yaccumArr - 1; ny <= yaccumArr + 1 && centerIsMax; ny++)
                {
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    if (nx == xaccumArr && ny == yaccumArr) continue;
                    if (accumArr[xaccumArr, yaccumArr] < accumArr[nx, ny])
                        centerIsMax = false;
                }
                
                if (centerIsMax) { accumArrMax[xaccumArr, yaccumArr] = accumArr[xaccumArr, yaccumArr];}
                else { accumArrMax[xaccumArr, yaccumArr] = 0;}
            }
            
            var peaks = new List<(int r, int theta)>();
            for (int xaccumArr = 0; xaccumArr < w; xaccumArr ++)
            for (int yaccumArr = 0; yaccumArr < h; yaccumArr++)
            {
                if(accumArrMax[xaccumArr, yaccumArr] < tPeak)
                {
                    accumArrMax[xaccumArr, yaccumArr] = 0;
                }
                else
                {
                    peaks.Add((yaccumArr, xaccumArr));
                }
            }
            
            return (peaks, AccumulatorToByte(accumArrMax));
        }

        private (byte[,,], List<((int, int), (int, int))>) HoughLineSegments(byte[,] inputImage, List<(int, int)> peaks, int minThresh, int minSegLen, int maxGap)
        {
            byte[,,] output = new byte[inputImage.GetLength(0), inputImage.GetLength(1), 3];

            int offsetX = inputImage.GetLength(0)/2;
            int offsetY = inputImage.GetLength(1)/2;
            int maxR = inputImage.GetLength(0) - 1 - offsetX + inputImage.GetLength(1) - 1 - offsetY;
            
            // make inputImage a color image
            Parallel.For(0, inputImage.GetLength(0), x =>
            {
                for (int y = 0; y < inputImage.GetLength(1); y++) 
                {
                    output[x, y, 0] = inputImage[x, y];
                    output[x, y, 1] = inputImage[x, y];
                    output[x, y, 2] = inputImage[x, y];
                }
            });

            List<((int, int), (int, int))> lines = new List<((int, int), (int, int))>();
            List<LineSegment> segments = new List<LineSegment>();

            foreach ((int, int) pair in peaks) {
                int theta = pair.Item2;
                int rshifted = pair.Item1; //i think the r was shifted in houghtrasnform soo we may need to reshift it
                int r = rshifted - maxR; 

                // was theta in degrees before? if yes i think we need to put it in radius, not sure though...
                float thetaRad = theta * MathF.PI / 180.0f;
                float cosT = MathF.Cos(thetaRad);
                float sinT = MathF.Sin(thetaRad);

                List<(int x, int y, bool on)> following = new List<(int x, int y, bool on)>();

                // get 'on' pixels
                if (MathF.Abs(sinT) >= MathF.Abs(cosT)) // crashs' when we try to do sin of 90 or 180 (cause div by 0 is impossible :,))
                {
                    for (int ix = 0; ix < inputImage.GetLength(0); ix++)
                    {
                        int iy = ((int)Math.Round((r - (ix - offsetX) * cosT) / sinT)) + offsetY;
                        if (iy >= 0 && iy < inputImage.GetLength(1))
                        {
                            following.Add((ix, iy, inputImage[ix, iy] > minThresh));
                            //if (inputImage[x, y] > minThresh) maybeOnLine.Add((x, y));
                        }
                    }
                }
                else
                {
                    // for the strict vertical lines (otherwise it )
                    for (int iy = 0; iy < inputImage.GetLength(1); iy++)
                    {
                        int ix = (int)MathF.Round((r - (iy - offsetY) * sinT) / cosT) + offsetX;
                        if (ix >= 0 && ix < inputImage.GetLength(0))
                        { 
                            following.Add((ix, iy, inputImage[ix, iy] > minThresh));
                        }
                    }
                }

                List<(int, int)> current = new List<(int, int)>();
                int gap = 0;

                foreach (var pixel in following)
                {
                    if (pixel.on)
                    {
                        current.Add((pixel.x, pixel.y));
                        gap = 0;
                    }
                    else if (current.Count > 0)
                    {
                        gap++;
                        if (gap > maxGap)          // gap too large: the segment ends here
                        {
                            FinishSegment(current);
                            current = new List<(int, int)>();
                            gap = 0;
                        }
                    }
                }
                FinishSegment(current);  
            } 

            return (output, lines);

            void FinishSegment(List<(int, int)> pts)
            {
                if (pts.Count == 0 || pts.Count < minSegLen) return;

                lines.Add((pts[0], pts[pts.Count - 1]));
                foreach ((int, int) point in pts)
                {
                    output[point.Item1, point.Item2, 0] = 255;
                    output[point.Item1, point.Item2, 1] = 0;
                    output[point.Item1, point.Item2, 2] = 0;
                }
            }

            float dist((int, int) p1, (int, int) p2)
            {
                return MathF.Sqrt(MathF.Pow(p2.Item2 - p1.Item2, 2) + MathF.Pow(p2.Item1 - p1.Item1, 2));
            }
        }


        struct LineSegment
        {
             public (int, int) startCoord;
             public (int, int) endCoord;
             public List<(int, int)> points; 
        };

        private byte[,,] BinaryPipeline(byte[,] inputImage, byte t_mag, int t_peak, int minThresh, int minSegLen, int maxGap)
        {
            // make edgemap
            byte[,] temp = ConvolveImage(inputImage, CreateGaussianFilter(5, 1.0f));
            temp = EdgeMagnitude(temp, PrewittHorizontal, PrewittVertical);
            temp = ThresholdImage(temp, t_mag);

            // hough transform and line segments
            int[,] accArr = HoughTransform(temp);
            var (peaks, _) = HoughPeakFinding(accArr, t_peak);
            (byte[,,] output, List<((int, int),(int, int))> lines) = HoughLineSegments(temp, peaks, minThresh, minSegLen, maxGap);

            return output;
        }

        private byte[,,] GrayscalePipeline(byte[,] inputImage, int t_peak, int minThresh, int minSegLen, int maxGap)
        {
            // make edgemap
            byte[,] temp = ConvolveImage(inputImage, CreateGaussianFilter(5, 1.0f));
            temp = EdgeMagnitude(temp, PrewittHorizontal, PrewittVertical);

            // hough transform and line segments
            int[,] accArr = HoughTransform(temp);
            var (peaks, _) = HoughPeakFinding(accArr, t_peak);
            (byte[,,] output, List<((int, int),(int, int))> lines) = HoughLineSegments(temp, peaks, minThresh, minSegLen, maxGap);

            return output;
        }        


        private byte[,] FloodFill(byte[,] inputImage)
        {
            int w = inputImage.GetLength(0);
            int h = inputImage.GetLength(1);
            const byte foreground = 255;

            byte[,] labels = new byte[w, h];
            byte nextLabel = 1;

            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if (inputImage[x, y] != foreground || labels[x, y] != 0)
                    continue;

                var queue = new Queue<(int x, int y)>();
                queue.Enqueue((x, y));
                labels[x, y] = nextLabel;

                while (queue.Count > 0)
                {
                    var (cx, cy) = queue.Dequeue();

                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;

                        int nx = cx + dx;
                        int ny = cy + dy;

                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        if (inputImage[nx, ny] != foreground) continue;
                        if (labels[nx, ny] != 0) continue;

                        labels[nx, ny] = nextLabel;
                        queue.Enqueue((nx, ny));
                    }
                }

                nextLabel++;

                if (nextLabel == 0)
                throw new InvalidOperationException(
                    "More than 253 regions found: assignment assumption violated. " +
                    "FloodFill expects an image with fewer than 254 regions!");
            }

            return AdjustContrast(labels);
        }


        // ====================================================================
        // ==================== IMAGE <-> BITMAP HELPERS (given) =============
        // ====================================================================

        /// <summary>
        /// Builds a displayable and savable <see cref="WriteableBitmap"/> from a grayscale byte[,] array
        /// (replicated into R, G, B; alpha fully opaque), using Avalonia's native imaging APIs.
        /// </summary>
        /// <param name="gray">The 2D grayscale byte array.</param>
        /// <returns>A displayable and savable <see cref="WriteableBitmap"/>.</returns>
        private WriteableBitmap ByteArrayToBitmap(byte[,] gray)
        {
            int w = gray.GetLength(0);
            int h = gray.GetLength(1);
            var size = new PixelSize(w, h);
            var bmp = new WriteableBitmap(
                size,
                new(96, 96),
                PixelFormat.Rgba8888,
                AlphaFormat.Opaque
            );

            using var fb = bmp.Lock();

            int totalBytes = fb.RowBytes * h;
            byte[] buffer = new byte[totalBytes];
            for (int y = 0; y < h; y++)
            {
                int rowStart = y * fb.RowBytes;
                for (int x = 0; x < w; x++)
                {
                    byte val = gray[x, y];
                    int idx = rowStart + x * 4;
                    buffer[idx + 0] = val; // R
                    buffer[idx + 1] = val; // G
                    buffer[idx + 2] = val; // B
                    buffer[idx + 3] = 255; // A (fully opaque)
                }
            }

            Marshal.Copy(buffer, 0, fb.Address, totalBytes);

            return bmp;
        }

        private WriteableBitmap ColorArrayToBitmap(byte[,,] color)
        {
            int w = color.GetLength(0);
            int h = color.GetLength(1);
            var bmp = new WriteableBitmap(new PixelSize(w, h), new(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
            using var fb = bmp.Lock();
            byte[] buffer = new byte[fb.RowBytes * h];
            for (int y = 0; y < h; y++)
            {
                int rowStart = y * fb.RowBytes;
                for (int x = 0; x < w; x++)
                {
                    int idx = rowStart + x * 4;
                    buffer[idx + 0] = color[x, y, 0]; // R
                    buffer[idx + 1] = color[x, y, 1]; // G
                    buffer[idx + 2] = color[x, y, 2]; // B
                    buffer[idx + 3] = 255;
                }
            }
            Marshal.Copy(buffer, 0, fb.Address, buffer.Length);
            return bmp;
        }
    }
}