# Fast and Robust Image Downloader
A tool suitable for downloading millions of images given a list of image URLs — a common task in image-related research.

I developed this tool to facilitate my own research. In contrast to a general purpose downloader, such as *[wget](https://www.gnu.org/software/wget/)*, this tool has multiple benefits designed for large-scale image downloading within the shortest possible time.

-----------------------------------

## Highlights

- Easily run tasks with multiple downloading threads and processes. Suitable for task with millions of images or more.

- **Ultra-fast:** Designed to well handle dead links and very large files. For speed boosting, I suggest a "multi-pass" approach: On the first pass, download small files only. This will quickly fulfill most of the links. On the second, third, and more passes, download larger and slower files. If a site went down for a moment, this approach would help catch the file when the site is up. The multi-pass approach can be configured easily.

- **Robust:** Can recover a previous download from a power-off event. Can try failed downloads again at any time automatically.

- **Image verification:** Apply image integrity check after download.

- Verbose log.

*Technically, this tool elegantly maintains a log that tracks the downloading status of each link with thread-safety. The above highlights are the benefits of this design.*

## Settings

All settings are configured in *config.ini* at the same path along with the executable. The followings are the important parameters.

[UrlListFile]

In this file, each line is a URL of an image with an image ID in the format of `{image_url}\t{image_id}`. The image ID is optional. If omitted, set *IsUseImageIDInUrlList* to false.

[ImageRecordRange]

Say you need to download 1 million images, you want to split the task by 100,000 URLs for each downloading process. The *ImageRecordRange* for the first process is 1-100000; second process is 100001-200000; and so on. You may start 5-10 downloading processes altogether, depending on your network speed.

[ConcurrentThreads]

Number of maximum concurrent downloads. As downloading is not CPU-intensive and each thread takes time to start a connection, it's totally fine to try more than the number of CPU cores.

[ThreadSurvivingTime]

Tune this parameter for multi-pass approach. For Pass 1: you can start with 12 seconds for each downloading thread. Pass 2: you may choose 20 seconds. Pass 3: try one minute or more to finish large files. Stop at an early pass if you don't want to spend time downloading the slow files.

[IsTryFailedDownload]

This option is handy for retrying links that were reported to have *FileNotExist* or other errors. Note that time-out links will always be retried in a new pass.

[IsFastValidation]

Set true if you have limited computational resources. By default (false), it tries to open each downloaded file as an image for validation. This can be rather time-consuming.
