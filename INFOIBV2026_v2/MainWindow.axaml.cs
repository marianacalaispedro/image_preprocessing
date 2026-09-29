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

namespace ImageApp
{
    public partial class MainWindow : Window
    {
        private WriteableBitmap? _loadedBitmap; // the raw loaded image, kept in color, for display in OriginalImage
        private byte[,,]? _loadedColorPixels; // [x, y, channel] with channel 0=R, 1=G, 2=B -- extracted once at load time
        private byte[,]? _processedGray; // Processed grayscale values (nullable)

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

                _processedGray = null;
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
            if (_processedGray == null)
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

                using var bmp = ByteArrayToBitmap(_processedGray);
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

                    switch (selected)
                    {
                        case ProcessingFunctions.ConvertToGrayscale:
                            // Already fully working; gray already holds the grayscale
                            // conversion result at this point (computed above, before this switch),
                            // so nothing further is needed here.
                            break;
                        case ProcessingFunctions.InvertImage:
                            gray = InvertImage(gray);
                            break;
                        case ProcessingFunctions.AdjustContrast:
                            gray = AdjustContrast(gray);
                            break;
                        case ProcessingFunctions.ConvolveImage:
                            gray = ConvolveImage(gray, CreateGaussianFilter(5, 1.0f));
                            break;
                        case ProcessingFunctions.MedianFilter:
                            gray = MedianFilter(gray, 5);
                            break;
                        case ProcessingFunctions.EdgeMagnitude:
                        {
                            sbyte[,] horizontalKernel = {{-1, 0, 1}, {-1, 0, 1}, {-1, 0, 1}}; // Define this kernel yourself --> prewitt (noise resistant)
                            sbyte[,] verticalKernel = {{-1, -1, -1}, {0, 0, 0}, {1, 1, 1}}; // Define this kernel yourself --> --> prewitt (noise resistant)
                            gray = EdgeMagnitude(gray, horizontalKernel, verticalKernel);
                            break;
                        }
                        case ProcessingFunctions.ThresholdImage:
                            gray = ThresholdImage(gray, _threshold);
                            break;

                        case ProcessingFunctions.BinaryErodeImage:
                        {
                            bool[,] structElem = null; // Define this structuring element yourself
                            gray = BinaryErodeImage(gray, structElem);
                            break;
                        }

                        case ProcessingFunctions.BinaryDilateImage:
                        {
                            bool[,] structElem = null; // Define this structuring element yourself
                            gray = BinaryDilateImage(gray, structElem);
                            break;
                        }

                        case ProcessingFunctions.BinaryOpenImage:
                        {
                            bool[,] structElem = null; // Define this structuring element yourself
                            gray = BinaryOpenImage(gray, structElem);
                            break;
                        }

                        case ProcessingFunctions.BinaryCloseImage:
                        {
                            bool[,] structElem = null; // Define this structuring element yourself
                            gray = BinaryCloseImage(gray, structElem);
                            break;
                        }

                        case ProcessingFunctions.GrayscaleErodeImage:
                        {
                            int[,] grayStructElem = null; // Define this structuring element yourself
                            gray = GrayscaleErodeImage(gray, grayStructElem);
                            break;
                        }

                        case ProcessingFunctions.GrayscaleDilateImage:
                        {
                            int[,] grayStructElem = null; // Define this structuring element yourself
                            gray = GrayscaleDilateImage(gray, grayStructElem);
                            break;
                        }

                        default:
                            throw new NotSupportedException($"Operation '{selected}' is not implemented in the OnApply switch.");
                    }

                    var bmp = ByteArrayToBitmap(gray);
                    return (gray, bmp);
                });

                _processedGray = resultGray;
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
                    for (int i = 0; i < kernelSize ; i++)
                    {
                        window.Clear();
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

            // TODO: add your functionality and checks, think about border handling and type conversion (negative values!)
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
            {
            for (int y = 0; y < inputImage.GetLength(1); y++)
            {

                tempImage[x, y] = (byte)Math.Min(threshold, inputImage[x, y]);
            }
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
            // TODO: implement binary erosion
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
            // TODO: implement binary dilation
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
            // TODO: implement binary opening
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
            // TODO: implement binary closing
            return output;
        }

        /// <summary>
        /// Performs morphological grayscale erosion using the provided structuring element.
        /// </summary>
        /// <param name="inputImage">The 2D grayscale input image.</param>
        /// <param name="structElem">2D integer structuring element defining neighborhood offsets.</param>
        /// <returns>The eroded grayscale image.</returns>
        private byte[,] GrayscaleErodeImage(byte[,] inputImage, int[,] structElem)
        {
            byte[,] output = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];
            // TODO: implement grayscale erosion
            return output;
        }

        /// <summary>
        /// Performs morphological grayscale dilation using the provided structuring element.
        /// </summary>
        /// <param name="inputImage">The 2D grayscale input image.</param>
        /// <param name="structElem">2D integer structuring element defining neighborhood offsets.</param>
        /// <returns>The dilated grayscale image.</returns>
        private byte[,] GrayscaleDilateImage(byte[,] inputImage, int[,] structElem)
        {
            byte[,] output = new byte[inputImage.GetLength(0), inputImage.GetLength(1)];
            // TODO: implement grayscale dilation
            return output;
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

        private byte[,] applyPointOperationToAll(byte[,] inputImage, Func<byte, byte> pointOperation)
        {
            for (int x = 0; x < inputImage.GetLength(0); x++)
            for (int y = 0; y < inputImage.GetLength(1); y++) 
            {
                inputImage[x, y] = pointOperation(inputImage[x,y]);
            }
            return inputImage;
        }
    }
}