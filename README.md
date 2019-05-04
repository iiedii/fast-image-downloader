# Fast and Robust Image Downloader
A tool suitable for downloading millions of images given a list of image URLs -- a common task in image-related research.

I developed this tool to facilitate my own research. In contrast to a general purpose downloader, such as *[wget](https://www.gnu.org/software/wget/)*, this tool has multiple benefits designed for large-scale image downloading within the shortest possible time.

-----------------------------------

## Highlights

- Easily run tasks with multiple downloading threads and processes.

- Ultra-fast: Designed to well handle dead links and very large files. For speed boosting, I suggest a "multi-pass" approach: On the first pass, download small files only. This will quickly fulfill most of the links. On the second, third, and more passes, download larger and slower files. If a site went down for a moment, this approach apparently would help catch the file too. The multi-pass approach can be configured easily.

- Robust: Can recover a previous download from a power-off event. Can try failed downloads again at any time.

- Image verification: A post-download image integrity check.

- Verbose log.
