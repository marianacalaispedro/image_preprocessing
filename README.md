Specification of what GUI elements have effect on what functions/task (other than the input image and the apply button)

[] ConvertToGrayScale - N/A
[] InvertImage - N/A
[] AdjustContrast - N/A
[] ConvolveImage - N/A (Always creates a Guassian Filter of size 5 and sigma 1.0)
[] MedianFilter - N/A (Always creates a Median Filter of size 5)
[] EdgeMagnitude - N/A
[] ThresholdImage - N/A (Always threshold with a value of 128)
[] BinaryErodeImage - StructElem size
[] BinaryDilateImage - StructElem size
[] BinaryOpenImage - StructElem size
[] BinaryCloseImage - StructElem size
[] GrayscaleErodeImage - N/A (Always uses the grayscale structure element below)
[] GrayscaleDilateImage - N/A (Always uses the grayscale structure element below)
[] Task1 - Filter + Kernel size + Sigma + Threshold
[] Task2 - StructElem size
[] Task3 - StructElem size

Grayscale structure element:

 |0|2|0|
---------
0|2|3|2|0
---------
2|3|5|3|2
---------
0|2|3|2|0
---------
 |0|2|0|
