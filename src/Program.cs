/*********************************************************
 *   Fast and Robust Image Downloader
 *      - Written by <Yijie Lu> on Jun 4, 2013
 *      - For research only
 *   Pros:
 *      - Parallel downloading
 *      - Disaster recovery
 *      - Filename auto-indexing
 *      - Flexible usage
 *      - Safety check and elaborate runtime logs
 *      - Duplicate urls will be ignored
 *   Cons:
 *      - Manual task split for multiple processes
 *      - Image verification (if enabled) may be slow
 *********************************************************/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;

namespace ImageDownloader
{
    class Program
    {
        public static void ReadConfigFile()
        {
            using (StreamReader srConfigFile = new StreamReader(configFile))
            {
                string line = null;
                int lineIndex = 0;

                try
                {
                    while ((line = srConfigFile.ReadLine()) != null)
                    {
                        lineIndex++;
                        if (line == "" || line.Trim()[0] == ';')
                            continue;

                        string[] splitLine = line.Split('=', ';');
                        string varName = splitLine[0].Trim();

                        switch (varName)
                        {
                            case "UrlListFile":
                                imageUrlListFile = splitLine[1].Trim();
                                imageUrlListFile = imageUrlListFile.Trim('\"');
                                break;
                            case "ImageDir":
                                imageDir = splitLine[1].Trim();
                                imageDir = imageDir.Trim('\"');
                                if (!imageDir.EndsWith("\\"))
                                    imageDir += "\\";
                                break;
                            case "ConcurrentThreads":
                                nConcurrentThreads = int.Parse(splitLine[1]);
                                nConcurrentThreads = (nConcurrentThreads < 1) ? 1 : nConcurrentThreads;
                                break;
                            case "ValidationThreads":
                                nValidationThreads = int.Parse(splitLine[1]);
                                nValidationThreads = (nValidationThreads < 1) ? 1 : nValidationThreads;
                                break;
                            case "ThreadSurvivingTime":
                                survivingTimeThreshold = long.Parse(splitLine[1]);
                                survivingTimeThreshold = (survivingTimeThreshold < 10) ? 10 : survivingTimeThreshold;
                                break;
                            case "ImagesInOneFolder":
                                nImagesInOneFolder = int.Parse(splitLine[1]);
                                nImagesInOneFolder = (nImagesInOneFolder < 100) ? 100 : nImagesInOneFolder;
                                break;
                            case "IsForceNewDownload":
                                isForceNewDownload = bool.Parse(splitLine[1]);
                                break;
                            case "IsUseImageIDInUrlList":
                                isUseImageIDInUrlList = bool.Parse(splitLine[1]);
                                break;
                            case "IsTryFailedDownload":
                                isTryFailedDownload = bool.Parse(splitLine[1]);
                                break;
                            case "IsFastValidation":
                                isFastValidation = bool.Parse(splitLine[1]);
                                break;
                            case "IsValidationOnly":
                                isValidationOnly = bool.Parse(splitLine[1]);
                                break;
                            case "ImageRecordRange":
                                if (splitLine[1].Trim().ToLower() == "all")
                                    isDownloadAllImages = true;
                                else
                                {
                                    string[] validImageIDRange = splitLine[1].Trim().Split('-');
                                    uint imageID1 = uint.Parse(validImageIDRange[0]);
                                    uint imageID2 = uint.Parse(validImageIDRange[1]);
                                    minImageIDForDownloading = (imageID1 < imageID2) ? imageID1 : imageID2;
                                    maxImageIDForDownloading = (imageID2 > imageID1) ? imageID2 : imageID1;
                                    isDownloadAllImages = false;
                                }
                                break;
                            default:
                                break;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    string errMessage = string.Format("Error reading config file at Line {0}: {1}", lineIndex, ex.Message);
                    throw new FormatException(errMessage);
                }
            }
        }

        public static void ReadUrlListFile()
        {
            imageUrlList = new Dictionary<string, uint>();
            invertedImageUrlList = new Dictionary<uint, string>();
            Console.WriteLine("Reading UrlList...");

            using (StreamReader srUrlList = new StreamReader(imageUrlListFile))
            {
                string line = null;
                uint lineIndex = 0;

                try
                {
                    string imageUrl = null;
                    uint imageID = 0;
                    int nDupUrls = 0;
                    while ((line = srUrlList.ReadLine()) != null)
                    {
                        lineIndex++;
                        line = line.Trim();
                        if (line == "" || line[0] == ';')
                            continue;

                        if (isUseImageIDInUrlList)
                        {
                            string[] splitLine = line.Split('\t');
                            imageUrl = splitLine[0].Trim();
                            imageID = uint.Parse(splitLine[1]);         // use ImageID specified in UrlList file
                        }
                        else
                        {
                            imageUrl = line.Trim();
                        }

                        if (!imageUrlList.ContainsKey(imageUrl))
                            imageUrlList.Add(imageUrl, imageID);
                        else
                        {
                            nDupUrls++;
                            //Console.WriteLine("   - Warning: Ignoring duplicate image URL at line {0}.", lineIndex);
                            continue;
                        }
                        if (!invertedImageUrlList.ContainsKey(imageID))
                            invertedImageUrlList.Add(imageID, imageUrl);
                        else
                        {
                            imageUrlList.Remove(imageUrl);
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("   - Warning: Ignoring duplicate ImageID at line {0}, URL is DISCARDED!", lineIndex);
                            Console.ResetColor();
                            continue;
                        }

                        imageID++;
                    }
                    if (nDupUrls != 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("   - Warning: {0} duplicate image URLs are ignored.", nDupUrls);
                        Console.ResetColor();
                    }
                }
                catch (System.Exception ex)
                {
                    string errMessage = string.Format("Error reading UrlList file at Line {0}: {1}", lineIndex, ex.Message);
                    throw new FormatException(errMessage);
                }
            }

            Console.WriteLine("UrlList done.");
            Console.WriteLine();
        }

        public static void ReadDownloadLogFile()
        {
            //---------------------------------------------------------------------
            //  ! This function does not use ImageID and FilePath in log file
            //  ! Be aware of the consistency between UrlList file and log file
            //    Log file format:
            //           {ImageID}\t{Status}\t{ImageURL}\t{FilePath}
            //---------------------------------------------------------------------

            string downloadLogFile = imageDir + "Download" + ".log";
            if (isForceNewDownload || !File.Exists(downloadLogFile))
            {
                imageDownloadStatus = null;         // indicates a new download should be started
                return;
            }

            Console.WriteLine("Previous download is found. Trying to recover...");
            imageDownloadStatus = new Dictionary<uint, DownloadedStatus.Status>();
            using (StreamReader srDownloadLog = new StreamReader(downloadLogFile))
            {
                string line = null;
                uint lineIndex = 0;

                try
                {
                    while ((line = srDownloadLog.ReadLine()) != null)
                    {
                        lineIndex++;
                        if (line.Trim() == "")
                            continue;

                        string[] splitLine = line.Trim().Split('\t');
                        DownloadLogItem logItem = new DownloadLogItem();
                        //logItem.ImageID = uint.Parse(splitLine[0]);
                        logItem.DownloadStatus = DownloadedStatus.Parse(splitLine[1]);
                        logItem.ImageUrl = splitLine[2].Trim();
                        logItem.ImageID = imageUrlList[logItem.ImageUrl];
                        //logItem.FilePath = splitLine[3].Trim();
                        logItem.FilePath = null;
                        imageDownloadStatus[logItem.ImageID] = logItem.DownloadStatus;          // new status will overwrite the old
                    }
                }
                catch (System.Exception ex)
                {
                    string errMessage = string.Format("Corrupted download record at Line {0}: {1}", lineIndex, ex.Message);
                    throw new FormatException(errMessage);
                }
            }

            Console.WriteLine("Previous download is acquired.");
            Console.WriteLine();
        }

        public static void MakeDownloadList()
        {
            downloadImageIDList = new Queue<uint>();
            imagePathList = new Dictionary<uint, string>();

            // Generate download image list
            Console.WriteLine("Making download image list...");
            if (imageDownloadStatus == null)            // a new download should be started
            {
                foreach (uint imageID in invertedImageUrlList.Keys)
                {
                    if (!isDownloadAllImages && ((imageID < minImageIDForDownloading) || (imageID > maxImageIDForDownloading)))
                        continue;
                    downloadImageIDList.Enqueue(imageID);
                }
            }
            else
            {
                foreach (uint imageID in invertedImageUrlList.Keys)
                {
                    if (!isDownloadAllImages && ((imageID < minImageIDForDownloading) || (imageID > maxImageIDForDownloading)))
                        continue;

                    if (imageDownloadStatus.ContainsKey(imageID))
                    {
                        DownloadedStatus.Status status = imageDownloadStatus[imageID];
                        switch (status)
                        {
                            case DownloadedStatus.Status.Success:
                                continue;
                            case DownloadedStatus.Status.TimeOut:       // TimeOut will be retried
                                break;
                            case DownloadedStatus.Status.VFailed:       // verification failure will be retried
                                break;
                            case DownloadedStatus.Status.FileNotExist:
                                if (!isTryFailedDownload)
                                    continue;
                                break;
                            case DownloadedStatus.Status.GeneralError:
                                if (!isTryFailedDownload)
                                    continue;
                                break;
                            case DownloadedStatus.Status.InvalidUrl:
                                continue;
                            default:
                                break;
                        }
                    }
                    downloadImageIDList.Enqueue(imageID);
                }
            }
            if (downloadImageIDList.Count == 0)
                throw new Exception("Nothing need to be downloaded");

            // Generate image filepath
            foreach (uint imageID in downloadImageIDList)
            {
                long fileID = imageID;
                long dirID = imageID / nImagesInOneFolder;
                string fileExt = getFileExtension(invertedImageUrlList[imageID]);
                string shortImagePath = dirID + "\\" + fileID + fileExt;
                imagePathList.Add(imageID, shortImagePath);
            }

            Console.WriteLine("#DownloadImages= " + downloadImageIDList.Count);
            Console.WriteLine();
        }

        #region File Extension Extractor
        private static string getFileExtension(string urlStr)
        {
            // Will return ".image" as general image extension if no known extension is found
            string urlStr_lower = urlStr.ToLower();
            Dictionary<int, List<string>> index_ExtList = new Dictionary<int, List<string>>();
            foreach (string imgExt in imageFileExtension)
            {
                int index = urlStr_lower.LastIndexOf(imgExt);
                if (!index_ExtList.ContainsKey(index))
                    index_ExtList.Add(index, new List<string>());
                index_ExtList[index].Add(imgExt);
            }
            int maxIndex = -1;
            foreach (int index in index_ExtList.Keys)
            {
                if (index > maxIndex)
                    maxIndex = index;
            }

            if (maxIndex == -1)     // no known extension is found
                return ".image";
            else
                return index_ExtList[maxIndex][0];
        }

        private static string[] imageFileExtension =
        {
            ".jpg", ".jpeg", ".gif", ".png", ".bmp", ".tiff", ".tif", ".wbmp", ".ico"
        };
        #endregion

        public static void SafetyCheckAndInit()
        {
            //-----------------------------------------------------------
            //  To ensure not to falsely overwrite existing downloads
            //  To initialize log files
            //-----------------------------------------------------------
            string downloadLogFile = imageDir + "Download" + ".log";
            string errorLogFile = imageDir + "Error" + ".log";
            string runtimeLogFile = imageDir + "Runtime" + ".log";
            string runtimeLogFile_PV = imageDir + "Runtime_ParallelValidation" + ".log";
            string reportFile = imageDir + "Report" + ".txt";
            string succeededDownloadLogFile = imageDir + "Download_Success" + ".log";
            string fileListFile = imageDir + "FileList" + ".txt";
            string fileListFile_NoGIF = imageDir + "FileList_NoGIF" + ".txt";
            string validationErrorLogFile = imageDir + "ValidationError" + ".log";
            string parameterFile = imageDir + "Parameter" + ".log";

            Directory.CreateDirectory(imageDir);
            if (isForceNewDownload)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("WARNING: WILL ERASE ALL PREVIOUS RECORDS AND START NEW DOWNLOAD!!!");
                Console.WriteLine("WARNING: WILL ERASE ALL PREVIOUS RECORDS AND START NEW DOWNLOAD!!!");
                Console.WriteLine("WARNING: WILL ERASE ALL PREVIOUS RECORDS AND START NEW DOWNLOAD!!!");
                Console.WriteLine("ABORT NOW if you are not sure what is going on...");
                Console.ResetColor();
                Thread.Sleep(10000);

                // delete existing file
                if (File.Exists(downloadLogFile))
                    File.Delete(downloadLogFile);
                if (File.Exists(errorLogFile))
                    File.Delete(errorLogFile);
                if (File.Exists(runtimeLogFile))
                    File.Delete(runtimeLogFile);
                if (File.Exists(runtimeLogFile_PV))
                    File.Delete(runtimeLogFile_PV);
                if (File.Exists(reportFile))
                    File.Delete(reportFile);
                if (File.Exists(succeededDownloadLogFile))
                    File.Delete(succeededDownloadLogFile);
                if (File.Exists(fileListFile))
                    File.Delete(fileListFile);
                if (File.Exists(fileListFile_NoGIF))
                    File.Delete(fileListFile_NoGIF);
                if (File.Exists(validationErrorLogFile))
                    File.Delete(validationErrorLogFile);
                if (File.Exists(parameterFile))
                    File.Delete(parameterFile);
                // create file
                using (FileStream fs = File.Create(downloadLogFile)) { }
                using (FileStream fs = File.Create(errorLogFile)) { }
                //using (FileStream fs = File.Create(reportFile)) { }
            }
            else
            {
                if (imageDownloadStatus == null || imageDownloadStatus.Count == 0)
                {
                    List<string> dirs = new List<string>(Directory.EnumerateDirectories(imageDir));
                    List<string> files = new List<string>(Directory.EnumerateFiles(imageDir));
                    // For security concerns, do not overwrite any existing files
                    if ((dirs.Count != 0) || (files.Count != 0))
                        throw new Exception("Output directory not empty. IsForceNewDownload = true is required to proceed.");

                    // Create file if secured
                    using (FileStream fs = File.Create(downloadLogFile)) { }
                    using (FileStream fs = File.Create(errorLogFile)) { }
                    //using (FileStream fs = File.Create(reportFile)) { }
                }
                else    // in recovery mode
                {
                    if (File.Exists(errorLogFile))          // error log file is intended ONLY to log errors within each execution
                        File.Delete(errorLogFile);
                    using (FileStream fs = File.Create(errorLogFile)) { }
                    if (File.Exists(runtimeLogFile))
                        File.Delete(runtimeLogFile);
                    if (File.Exists(runtimeLogFile_PV))
                        File.Delete(runtimeLogFile_PV);
                    if (File.Exists(reportFile))
                        File.Delete(reportFile);
                    if (File.Exists(validationErrorLogFile))
                        File.Delete(validationErrorLogFile);

                    // check the config parameter consistency from previous parameter log
                    try
                    {
                        using (StreamReader srParameter = new StreamReader(parameterFile))
                        {
                            string line = null;

                            while ((line = srParameter.ReadLine()) != null)
                            {
                                line = line.Trim();
                                if (line == "" || line[0] == ';')
                                    continue;

                                string[] splitLine = line.Split('=');
                                if (splitLine[0] == "ImageRecordRange")
                                {
                                    if (isDownloadAllImages)
                                    {
                                        if (splitLine[1].Trim().ToLower() != "all")
                                        {
                                            //throw new ArgumentException("ImageRecordRange does not match the previous download");
                                            Console.ForegroundColor = ConsoleColor.Yellow;
                                            Console.WriteLine("Warning: ImageRecordRange changed to ALL.");
                                            Console.ResetColor();
                                        }
                                    }
                                    else if (splitLine[1].Trim() != minImageIDForDownloading + "-" + maxImageIDForDownloading)
                                    {
                                        //throw new ArgumentException("ImageRecordRange does not match the previous download");
                                        Console.ForegroundColor = ConsoleColor.Yellow;
                                        Console.WriteLine("Warning: ImageRecordRange changed to {0}-{1}.", minImageIDForDownloading, maxImageIDForDownloading);
                                        Console.ResetColor();
                                    }
                                }
                                else if (splitLine[0] == "ImagesInOneFolder")
                                {
                                    if (nImagesInOneFolder != int.Parse(splitLine[1]))
                                        throw new ArgumentException("ImagesInOneFolder does not match the previous download");
                                }
                                else if (splitLine[0] == "IsUseImageIDInUrlList")
                                {
                                    if (isUseImageIDInUrlList != bool.Parse(splitLine[1]))
                                        throw new ArgumentException("IsUseImageIDInUrlList does not match the previous download");
                                }
                            }
                        }
                    }
                    catch (System.ArgumentException ae)
                    {
                        throw new ArgumentException(ae.Message);
                    }
                    catch (System.Exception)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("Warning: Failed to read previous parameter log. Parameter check is skipped.");
                        Console.ResetColor();
                    }
                }
            }

            Console.WriteLine("Safety is ensured and initialization is done. We're good to go.");
            Console.WriteLine();
            Thread.Sleep(7000);     // wait 7 seconds to start
        }

        public static void WriteParameterFile()
        {
            string parameterFile = imageDir + "Parameter" + ".log";
            using (StreamWriter swParameter = new StreamWriter(parameterFile))
            {
                swParameter.WriteLine("[ Parameters ]");
                swParameter.WriteLine("==============");
                if (isDownloadAllImages)
                    swParameter.WriteLine("ImageRecordRange= all");
                else
                    swParameter.WriteLine("ImageRecordRange= {0}-{1}", minImageIDForDownloading, maxImageIDForDownloading);
                swParameter.WriteLine("ConcurrentThreads= " + nConcurrentThreads);
                swParameter.WriteLine("ValidationThreads= " + nValidationThreads);
                swParameter.WriteLine("ThreadSurvivingTime= {0}ms", survivingTimeThreshold);
                swParameter.WriteLine("ImagesInOneFolder= " + nImagesInOneFolder);
                swParameter.WriteLine("IsForceNewDownload= " + isForceNewDownload);
                swParameter.WriteLine("IsUseImageIDInUrlList= " + isUseImageIDInUrlList);
                swParameter.WriteLine("IsTryFailedDownload= " + isTryFailedDownload);
                swParameter.WriteLine("IsFastValidation= " + isFastValidation);
                swParameter.WriteLine("IsValidationOnly= " + isValidationOnly);
            }
        }

        public static void DoDownload()
        {
            downloadingThreadCount = 0;         // current number of downloading threads
            nSuccessfulDownloads = 0;
            nGeneralErrorDownloads = 0;
            nTimeoutDownloads = 0;

            int nProcessedImages = 0;
            int totalImages = downloadImageIDList.Count;
            // Timeout killer
            killerList = new Dictionary<WebClient, Timer>();
            webClientRemovalBuffer_for_killerList = new Queue<WebClient>();
            // Output cache
            downloadLogOutputBuffer = new Queue<string>();
            errorLogOutputBuffer = new Queue<string>();

            Console.WriteLine("Downloading...");
            Console.CursorVisible = false;
            int cursorTop = Console.CursorTop;
            int cursorLeft = Console.CursorLeft;

            while (downloadImageIDList.Count != 0)
            {
                if (downloadingThreadCount < nConcurrentThreads)
                {
                    uint imageID = downloadImageIDList.Dequeue();
                    nProcessedImages++;

                    string imageUrlStr = invertedImageUrlList[imageID];
                    string shortImagePath = imagePathList[imageID];
                    string fullImagePath = imageDir + shortImagePath;
                    string thisImageFileDir = (new FileInfo(fullImagePath)).DirectoryName;
                    Directory.CreateDirectory(thisImageFileDir);

                    Uri imageUrl = null;
                    try
                    {
                        imageUrl = new Uri(imageUrlStr);
                    }
                    catch (System.Exception)
                    {
                        writeDownloadLog(imageID, downloadLogOutputBuffer, DownloadedStatus.Status.InvalidUrl);
                        writeErrorLog(imageID, errorLogOutputBuffer, DownloadedStatus.Status.InvalidUrl);
                        continue;
                    }

                    downloadingThreadCount++;
                    downloadImage(imageUrl, fullImagePath, imageID);

                    double percentage = (double)nProcessedImages / (double)totalImages * 100.0;
                    Console.WriteLine("  - Processing Image {0}/{1}, {2:F1}%       ", nProcessedImages, totalImages, percentage);
                    Console.WriteLine("  - SUCC:TOUT:ERR = [ {0}:{1}:{2} ]       ", nSuccessfulDownloads, nTimeoutDownloads, nGeneralErrorDownloads);
                    Console.SetCursorPosition(cursorLeft, cursorTop);
                }
                if (errorLogOutputBuffer.Count > nConcurrentThreads)
                    throw new InternalBufferOverflowException("Error log buffer overflow");
                if (downloadLogOutputBuffer.Count > nConcurrentThreads)      // shouldn't be reached
                    throw new InternalBufferOverflowException("Download log buffer overflow");

                // Do some cleaning
                while (webClientRemovalBuffer_for_killerList.Count != 0)
                {
                    WebClient webClient = webClientRemovalBuffer_for_killerList.Dequeue();
                    if (webClient != null && killerList.ContainsKey(webClient))
                    {
                        Timer timer = killerList[webClient];
                        timer.Dispose();        // always be careful about manual disposal
                        killerList.Remove(webClient);
                    }
                }

                Thread.Sleep(50);       // save CPU
            }
            Thread.Sleep((int)survivingTimeThreshold);          // wait completion of all downloads
            Console.CursorVisible = true;
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("All downloads done.");
            Console.WriteLine();
        }

        #region Validation Function
        public static void ValidateDownloading()
        {
            //-----------------------------------------------------------------------
            //  To validate downloading and make a final image list with indexing
            //  To write download report
            //-----------------------------------------------------------------------
            string succeededDownloadLogFile = imageDir + "Download_Success" + ".log";
            string validationErrorLogFile = imageDir + "ValidationError" + ".log";
            string fileListFile = imageDir + "FileList" + ".txt";
            string fileListFile_NoGIF = imageDir + "FileList_NoGIF" + ".txt";
            string reportFile = imageDir + "Report" + ".txt";
            string runtimeLogFile = imageDir + "Runtime" + ".log";
            string runtimeLogFile_PV = imageDir + "Runtime_ParallelValidation" + ".log";
            int nSuccess = 0;
            int nValidatedSuccess = 0;
            int nTimeOut = 0;
            int nGeneralError = 0;
            Queue<string> validationErrLogBuffer = new Queue<string>();

            Console.WriteLine("Validating successful downloads, read download log...");
            ReadDownloadLogFile();

            Console.WriteLine("Validating...");

            if (imageDownloadStatus == null)
                throw new Exception("No download log found");

            // initialize log files
            if (File.Exists(succeededDownloadLogFile))
                File.Delete(succeededDownloadLogFile);
            if (File.Exists(validationErrorLogFile))
                File.Delete(validationErrorLogFile);
            if (File.Exists(fileListFile))
                File.Delete(fileListFile);
            if (File.Exists(fileListFile_NoGIF))
                File.Delete(fileListFile_NoGIF);
            if (File.Exists(reportFile))
                File.Delete(reportFile);
            if (File.Exists(runtimeLogFile))
                File.Delete(runtimeLogFile);
            if (File.Exists(runtimeLogFile_PV))
                File.Delete(runtimeLogFile_PV);
            using (FileStream fs = File.Create(succeededDownloadLogFile)) { }
            using (FileStream fs = File.Create(fileListFile)) { }
            using (FileStream fs = File.Create(fileListFile_NoGIF)) { }
            using (FileStream fs = File.Create(runtimeLogFile_PV)) { }

            int count = 0;
            int totalCount = imageDownloadStatus.Count;
            Console.CursorVisible = false;
            int cursorTop = Console.CursorTop;
            int cursorLeft = Console.CursorLeft;
            Queue<string> outputBuffer_SuccLog = new Queue<string>();
            Queue<string> outputBuffer_fListFile = new Queue<string>();
            Queue<string> outputBuffer_fListFile_NoGIF = new Queue<string>();
            SemaphoreSlim semaphoreConsole = new SemaphoreSlim(1);
            ParallelOptions parallelOptions = new ParallelOptions();
            parallelOptions.MaxDegreeOfParallelism = nValidationThreads;

            try
            {
                Parallel.ForEach<KeyValuePair<uint, DownloadedStatus.Status>>(imageDownloadStatus, parallelOptions, (kvp) => parallelLoopValidation(kvp, ref count, totalCount, cursorLeft, cursorTop, ref nSuccess, ref nValidatedSuccess, ref nTimeOut, ref nGeneralError, ref outputBuffer_SuccLog, ref outputBuffer_fListFile, ref outputBuffer_fListFile_NoGIF, ref validationErrLogBuffer, ref semaphoreConsole));
            }
            catch (AggregateException ae)
            {
                ae.Handle(handleParallelValidationException);
            }

            if ((new FileInfo(runtimeLogFile_PV)).Length == 0)
                File.Delete(runtimeLogFile_PV);

            if (outputBuffer_SuccLog.Count != 0)
                using (StreamWriter swSuccDownloadLog = new StreamWriter(succeededDownloadLogFile, true))
            {
                while (outputBuffer_SuccLog.Count != 0)
                    swSuccDownloadLog.WriteLine(outputBuffer_SuccLog.Dequeue());
            }
            if (outputBuffer_fListFile.Count != 0)
                using (StreamWriter swFileList = new StreamWriter(fileListFile, true))
            {
                while (outputBuffer_fListFile.Count != 0)
                    swFileList.WriteLine(outputBuffer_fListFile.Dequeue());
            }
            if (outputBuffer_fListFile_NoGIF.Count != 0)
                using (StreamWriter swFileList_NoGIF = new StreamWriter(fileListFile_NoGIF, true))
            {
                while (outputBuffer_fListFile_NoGIF.Count != 0)
                    swFileList_NoGIF.WriteLine(outputBuffer_fListFile_NoGIF.Dequeue());
            }

            Console.WriteLine();
            Console.WriteLine();
            Console.CursorVisible = true;

            if (validationErrLogBuffer.Count != 0)
            {
                using (StreamWriter swValidationErrorLog = new StreamWriter(validationErrorLogFile))
                {
                    while (validationErrLogBuffer.Count != 0)
                    {
                        swValidationErrorLog.WriteLine(validationErrLogBuffer.Dequeue());
                    }
                }
            }

            using (StreamWriter swReport = new StreamWriter(reportFile))
            {
                swReport.WriteLine("< Report of Downloads >");
                swReport.WriteLine("TotalImageProcessed= " + imageDownloadStatus.Count);
                swReport.WriteLine("  - #ClaimedSuccess= " + nSuccess);
                swReport.WriteLine("  - #ValidatedSuccess= " + nValidatedSuccess);
                swReport.WriteLine("  - #GeneralError= " + nGeneralError);
                swReport.WriteLine("  - #Timeout= " + nTimeOut);
            }

            Console.WriteLine();
            Console.WriteLine("< Report of Downloads >");
            Console.WriteLine("TotalImageProcessed= " + imageDownloadStatus.Count);
            Console.WriteLine("  - #ClaimedSuccess= " + nSuccess);
            Console.WriteLine("  - #ValidatedSuccess= " + nValidatedSuccess);
            Console.WriteLine("  - #GeneralError= " + nGeneralError);
            Console.WriteLine("  - #Timeout= " + nTimeOut);
            Console.WriteLine();
        }

        private static bool handleParallelValidationException(Exception e)
        {
            string runtimeLogFile_PV = imageDir + "Runtime_ParallelValidation" + ".log";
            Directory.CreateDirectory(imageDir);
            using (StreamWriter swRuntimeLog = new StreamWriter(runtimeLogFile_PV, true))
            {
                swRuntimeLog.WriteLine("[{0}] One of the validation threads failed:", DateTime.Now);
                swRuntimeLog.WriteLine("   - Error Message: " + e.Message);
                swRuntimeLog.WriteLine("   - Target: " + e.TargetSite);
                swRuntimeLog.WriteLine();
                swRuntimeLog.WriteLine("--- Trace ---");
                swRuntimeLog.WriteLine(e.StackTrace);
                swRuntimeLog.WriteLine();
                swRuntimeLog.WriteLine();
            }

            return true;
        }

        private static void parallelLoopValidation(
            KeyValuePair<uint, DownloadedStatus.Status> kvp,
            ref int count,
            int totalCount,
            int cursorLeft,
            int cursorTop,
            ref int nSuccess,
            ref int nValidatedSuccess,
            ref int nTimeOut,
            ref int nGeneralError,
            ref Queue<string> outputBuffer_SuccLog,
            ref Queue<string> outputBuffer_fList,
            ref Queue<string> outputBuffer_fList_NoGIF,
            ref Queue<string> validationErrLogBuffer,
            ref SemaphoreSlim semaphoreConsole)
        {
            uint imageID = kvp.Key;
            DownloadedStatus.Status status = kvp.Value;

            count++;
            double percentage = (double)count / (double)totalCount * 100.0;
            if (semaphoreConsole.CurrentCount > 0)
            {
                semaphoreConsole.Wait();
                Console.SetCursorPosition(cursorLeft, cursorTop);
                Console.WriteLine("  - {0:F1}% validated       ", percentage);
                Console.SetCursorPosition(cursorLeft, cursorTop);
                semaphoreConsole.Release();
            }

            if (status == DownloadedStatus.Status.Success)
            {
                string imageUrl = invertedImageUrlList[imageID];
                long fileID = imageID;
                long dirID = imageID / nImagesInOneFolder;
                string fileExt = getFileExtension(imageUrl);
                string shortImagePath = dirID + "\\" + fileID + fileExt;
                string fullImagePath = imageDir + shortImagePath;

                nSuccess++;

                ImageType imgType;
                if (File.Exists(fullImagePath) && ((new FileInfo(fullImagePath)).Length != 0) && isValidGDIPlusImage(fullImagePath, out imgType))
                {
                    nValidatedSuccess++;
                    if (fileExt == ".image")
                    {
                        string oldFullImagePath = fullImagePath;
                        fileExt = "." + imgType;
                        shortImagePath = dirID + "\\" + fileID + fileExt;
                        fullImagePath = imageDir + shortImagePath;
                        if (imagePathList != null)
                            imagePathList[imageID] = shortImagePath;
                        File.Delete(fullImagePath);
                        File.Copy(oldFullImagePath, fullImagePath);
                    }
                    string thisLog = imageID + "\t" + status + "\t" + imageUrl + "\t" + shortImagePath;
                    //swSucceededDownloadLog.WriteLine(thisLog);
                    writeSuccDownloadLog(thisLog, ref outputBuffer_SuccLog);
                    //swFileList.WriteLine(fullImagePath);
                    writeFileList(fullImagePath, ref outputBuffer_fList);

                    if ((fileExt != ".gif") && (fileExt != ".image"))
                        writeFileList_NoGIF(fullImagePath, ref outputBuffer_fList_NoGIF);
                }
                else
                {
                    string vErrLog = imageID + "\t" + DownloadedStatus.Status.VFailed + "\t" + imageUrl + "\t" + shortImagePath;
                    lock (validationErrLogBuffer)
                    {
                        validationErrLogBuffer.Enqueue(vErrLog);
                    }
                    File.Delete(fullImagePath);
                }
            }
            else if (status == DownloadedStatus.Status.TimeOut)
                nTimeOut++;
            else
                nGeneralError++;
        }

        private static void writeSuccDownloadLog(string thisLog, ref Queue<string> outputBuffer)
        {
            string succDownloadLogFile = imageDir + "Download_Success" + ".log";

            try
            {
                if (outputBuffer.Count > 1987121)
                {
                    using (StreamWriter swSuccDownloadLog = new StreamWriter(succDownloadLogFile, true))
                    {
                        if (outputBuffer.Count != 0)
                        {
                            lock (outputBuffer)
                            {
                                while (outputBuffer.Count != 0)
                                    swSuccDownloadLog.WriteLine(outputBuffer.Dequeue());
                            }
                        }
                        swSuccDownloadLog.WriteLine(thisLog);
                    }
                }
                else
                {
                    lock (outputBuffer)
                    {
                        outputBuffer.Enqueue(thisLog);
                    }
                }
            }
            catch (System.Exception)
            {
                lock (outputBuffer)
                {
                    outputBuffer.Enqueue(thisLog);
                }
            }
        }

        private static void writeFileList(string thisFileName, ref Queue<string> outputBuffer)
        {
            string fileListFile = imageDir + "FileList" + ".txt";

            try
            {
                if (outputBuffer.Count > 1987121)
                {
                    using (StreamWriter swFileList = new StreamWriter(fileListFile, true))
                    {
                        if (outputBuffer.Count != 0)
                        {
                            lock (outputBuffer)
                            {
                                while (outputBuffer.Count != 0)
                                    swFileList.WriteLine(outputBuffer.Dequeue());
                            }
                        }
                        swFileList.WriteLine(thisFileName);
                    }
                }
                else
                {
                    lock (outputBuffer)
                    {
                        outputBuffer.Enqueue(thisFileName);
                    }
                }
            }
            catch (System.Exception)
            {
                lock (outputBuffer)
                {
                    outputBuffer.Enqueue(thisFileName);
                }
            }
        }

        private static void writeFileList_NoGIF(string thisFileName, ref Queue<string> outputBuffer)
        {
            string fileListFile_NoGIF = imageDir + "FileList_NoGIF" + ".txt";

            try
            {
                if (outputBuffer.Count > 1987121)
                {
                    using (StreamWriter swFileList_NoGIF = new StreamWriter(fileListFile_NoGIF, true))
                    {
                        if (outputBuffer.Count != 0)
                        {
                            lock (outputBuffer)
                            {
                                while (outputBuffer.Count != 0)
                                    swFileList_NoGIF.WriteLine(outputBuffer.Dequeue());
                            }
                        }
                        swFileList_NoGIF.WriteLine(thisFileName);
                    }
                }
                else
                {
                    lock (outputBuffer)
                    {
                        outputBuffer.Enqueue(thisFileName);
                    }
                }
            }
            catch (System.Exception)
            {
                lock (outputBuffer)
                {
                    outputBuffer.Enqueue(thisFileName);
                }
            }
        }

        public static void ValidateDownloading_Fast()
        {
            //-----------------------------------------------------------------------
            //  To validate downloading and make a final image list with indexing
            //  To write download report
            //  ** a fast version without checking the corrupted files
            //-----------------------------------------------------------------------
            string succeededDownloadLogFile = imageDir + "Download_Success" + ".log";
            string validationErrorLogFile = imageDir + "ValidationError" + ".log";
            string fileListFile = imageDir + "FileList" + ".txt";
            string fileListFile_NoGIF = imageDir + "FileList_NoGIF" + ".txt";
            string reportFile = imageDir + "Report" + ".txt";
            string runtimeLogFile = imageDir + "Runtime" + ".log";
            string runtimeLogFile_PV = imageDir + "Runtime_ParallelValidation" + ".log";
            int nSuccess = 0;
            int nValidatedSuccess = 0;
            int nTimeOut = 0;
            int nGeneralError = 0;
            Queue<string> validationErrLogBuffer = new Queue<string>();

            Console.WriteLine("Validating successful downloads, read download log...");
            ReadDownloadLogFile();

            Console.WriteLine("Validating...");

            // initialize log files
            if (File.Exists(succeededDownloadLogFile))
                File.Delete(succeededDownloadLogFile);
            if (File.Exists(validationErrorLogFile))
                File.Delete(validationErrorLogFile);
            if (File.Exists(fileListFile))
                File.Delete(fileListFile);
            if (File.Exists(fileListFile_NoGIF))
                File.Delete(fileListFile_NoGIF);
            if (File.Exists(reportFile))
                File.Delete(reportFile);
            if (File.Exists(runtimeLogFile))
                File.Delete(runtimeLogFile);
            if (File.Exists(runtimeLogFile_PV))
                File.Delete(runtimeLogFile_PV);

            if (imageDownloadStatus == null)
                throw new Exception("No download log found");
            using (StreamWriter swSucceededDownloadLog = new StreamWriter(succeededDownloadLogFile))
            {
                using (StreamWriter swFileList = new StreamWriter(fileListFile))
                {
                    using (StreamWriter swFileList_NoGif = new StreamWriter(fileListFile_NoGIF))
                    {
                        int count = 0;
                        int totalCount = imageDownloadStatus.Count;
                        Console.CursorVisible = false;
                        foreach (KeyValuePair<uint, DownloadedStatus.Status> kvp in imageDownloadStatus)
                        {
                            uint imageID = kvp.Key;
                            DownloadedStatus.Status status = kvp.Value;

                            count++;
                            double percentage = (double)count / (double)totalCount * 100.0;
                            int cursorTop = Console.CursorTop;
                            int cursorLeft = Console.CursorLeft;
                            Console.WriteLine("  - {0:F1}% verified        ", percentage);
                            Console.SetCursorPosition(cursorLeft, cursorTop);

                            if (status == DownloadedStatus.Status.Success)
                            {
                                string imageUrl = invertedImageUrlList[imageID];
                                long fileID = imageID;
                                long dirID = imageID / nImagesInOneFolder;
                                string fileExt = getFileExtension(imageUrl);
                                string shortImagePath = dirID + "\\" + fileID + fileExt;
                                string fullImagePath = imageDir + shortImagePath;

                                nSuccess++;

                                ImageType imgType;
                                if (File.Exists(fullImagePath) && ((new FileInfo(fullImagePath)).Length != 0))
                                {
                                    nValidatedSuccess++;
                                    if ((fileExt == ".image") && isValidGDIPlusImage(fullImagePath, out imgType))
                                    {
                                        string oldFullImagePath = fullImagePath;
                                        fileExt = "." + imgType;
                                        shortImagePath = dirID + "\\" + fileID + fileExt;
                                        fullImagePath = imageDir + shortImagePath;
                                        if ((oldFullImagePath != fullImagePath) && File.Exists(fullImagePath))
                                            File.Delete(fullImagePath);
                                        File.Copy(oldFullImagePath, fullImagePath);
                                    }
                                    string thisLog = imageID + "\t" + status + "\t" + imageUrl + "\t" + shortImagePath;
                                    swSucceededDownloadLog.WriteLine(thisLog);
                                    swFileList.WriteLine(fullImagePath);

                                    if ((fileExt != ".gif") && (fileExt != ".image"))
                                        swFileList_NoGif.WriteLine(fullImagePath);
                                }
                                else
                                {
                                    string vErrLog = imageID + "\t" + DownloadedStatus.Status.VFailed + "\t" + imageUrl + "\t" + shortImagePath;
                                    validationErrLogBuffer.Enqueue(vErrLog);
                                    File.Delete(fullImagePath);
                                }
                            }
                            else if (status == DownloadedStatus.Status.TimeOut)
                                nTimeOut++;
                            else
                                nGeneralError++;
                        }
                        Console.WriteLine();
                        Console.CursorVisible = true;
                    }
                }
            }

            if (validationErrLogBuffer.Count != 0)
            {
                using (StreamWriter swValidationErrorLog = new StreamWriter(validationErrorLogFile))
                {
                    while (validationErrLogBuffer.Count != 0)
                    {
                        swValidationErrorLog.WriteLine(validationErrLogBuffer.Dequeue());
                    }
                }
            }

            using (StreamWriter swReport = new StreamWriter(reportFile))
            {
                swReport.WriteLine("< Report of Downloads >");
                swReport.WriteLine("TotalImageProcessed= " + imageDownloadStatus.Count);
                swReport.WriteLine("  - #ClaimedSuccess= " + nSuccess);
                swReport.WriteLine("  - #ValidatedSuccess= " + nValidatedSuccess);
                swReport.WriteLine("  - #GeneralError= " + nGeneralError);
                swReport.WriteLine("  - #Timeout= " + nTimeOut);
            }

            Console.WriteLine();
            Console.WriteLine("< Report of Downloads >");
            Console.WriteLine("TotalImageProcessed= " + imageDownloadStatus.Count);
            Console.WriteLine("  - #ClaimedSuccess= " + nSuccess);
            Console.WriteLine("  - #ValidatedSuccess= " + nValidatedSuccess);
            Console.WriteLine("  - #GeneralError= " + nGeneralError);
            Console.WriteLine("  - #Timeout= " + nTimeOut);
            Console.WriteLine();
        }

        private static bool isValidGDIPlusImage(string filename, out ImageType imgType)
        {
            try
            {
                using (Bitmap bmp = new Bitmap(filename))
                {
                    ImageFormat bmpFormat = bmp.RawFormat;
                    if (bmpFormat.Equals(ImageFormat.Jpeg))
                        imgType = ImageType.jpg;
                    else if (bmpFormat.Equals(ImageFormat.Gif))
                        imgType = ImageType.gif;
                    else if (bmpFormat.Equals(ImageFormat.Png))
                        imgType = ImageType.png;
                    else if (bmpFormat.Equals(ImageFormat.Tiff))
                        imgType = ImageType.tiff;
                    else if (bmpFormat.Equals(ImageFormat.Bmp))
                        imgType = ImageType.bmp;
                    else if (bmpFormat.Equals(ImageFormat.Icon))
                        imgType = ImageType.ico;
                    else if (bmpFormat.Equals(ImageFormat.Exif))
                        imgType = ImageType.exif;
                    else if (bmpFormat.Equals(ImageFormat.Emf))
                        imgType = ImageType.emf;
                    else if (bmpFormat.Equals(ImageFormat.Wmf))
                        imgType = ImageType.wmf;
                    else
                        imgType = ImageType.image;
                    return true;
                }
            }
            catch (Exception)
            {
                imgType = ImageType._null;
                return false;
            }
        }
        #endregion

        #region AsyncDownload Function
        private static void downloadImage(Uri imageUrl, string outputFileName, uint downloadID)
        {
            WebClient webClient = new WebClient();
            TimerCallback tcbKillDownload = new TimerCallback(killDownload);
            webClient.DownloadFileCompleted += new AsyncCompletedEventHandler(webClient_DownloadFileCompleted);
            //webClient.DownloadProgressChanged += new DownloadProgressChangedEventHandler(webClient_DownloadProgressChanged);
            Timer killTimer = new Timer(tcbKillDownload, new WebclientDownloadIDType(webClient, downloadID), survivingTimeThreshold, Timeout.Infinite);
            try
            {
                killerList[webClient] = killTimer;
            }
            catch (System.Exception)
            {
                ;   //? it's OK if failed to add key, but why failed? failure makes dead thread possible
            }
            webClient.DownloadFileAsync(imageUrl, outputFileName, downloadID);
        }

        private static void killDownload(object stateInfo)
        {
            WebclientDownloadIDType wd = (WebclientDownloadIDType)stateInfo;
            wd.Webclient.CancelAsync();
            //killerList.Remove(wd.Webclient);
        }

        private static void deleteEmptyFile(string fullFilePath)
        {
            if (File.Exists(fullFilePath))
            {
                try
                {
                    if ((new FileInfo(fullFilePath)).Length == 0)
                    {
                        File.Delete(fullFilePath);
                    }
                }
                catch (System.Exception)
                {
                    ;   // it's OK when failed
                }
            }
        }

        //private T CastByExample<T>(object target, T example)
        //{
        //    return (T)target;
        //}

        private static void webClient_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        {
            // This function is called in concurrent thread!
            WebClient webClient = (WebClient)sender;
            uint downloadID = (uint)e.UserState;
            if (e.Cancelled)
            {
                writeDownloadLog((uint)e.UserState, downloadLogOutputBuffer, DownloadedStatus.Status.TimeOut);
                writeErrorLog((uint)e.UserState, errorLogOutputBuffer, DownloadedStatus.Status.TimeOut);
                downloadingThreadCount--;
                nTimeoutDownloads++;

                //Thread.Sleep(100);      // wait for WebClient close writing
                string fullImagePath = imageDir + imagePathList[downloadID];
                deleteEmptyFile(fullImagePath);
            }
            else if (e.Error != null)
            {
                writeDownloadLog((uint)e.UserState, downloadLogOutputBuffer, DownloadedStatus.Status.GeneralError);
                writeErrorLog((uint)e.UserState, errorLogOutputBuffer, DownloadedStatus.Status.GeneralError);
                downloadingThreadCount--;
                nGeneralErrorDownloads++;

                //Thread.Sleep(100);      // wait for WebClient close writing
                string fullImagePath = imageDir + imagePathList[downloadID];
                deleteEmptyFile(fullImagePath);
            }
            else    // success
            {
                Timer timer = killerList[webClient];
                timer.Change(Timeout.Infinite, Timeout.Infinite);       // stop the timer and wait for disposal
                //timer.Dispose();        // always be careful about manual disposal
                writeDownloadLog((uint)e.UserState, downloadLogOutputBuffer, DownloadedStatus.Status.Success);
                downloadingThreadCount--;
                nSuccessfulDownloads++;
            }

            lock (webClientRemovalBuffer_for_killerList)
            {
                webClientRemovalBuffer_for_killerList.Enqueue(webClient);
            }
        }

        private static void writeDownloadLog(uint downloadID, Queue<string> outputBuffer, DownloadedStatus.Status status)
        {
            string downloadLogFile = imageDir + "Download" + ".log";
            uint imageID = downloadID;
            string imageUrl = invertedImageUrlList[imageID];
            string shortImagePath = imagePathList[imageID];
            string thisLog = imageID + "\t" + status + "\t" + imageUrl + "\t" + shortImagePath;

            try
            {
                using (StreamWriter swDownloadLog = new StreamWriter(downloadLogFile, true))
                {
                    if (outputBuffer.Count != 0)
                    {
                        lock (outputBuffer)
                        {
                            while (outputBuffer.Count != 0)
                                swDownloadLog.WriteLine(outputBuffer.Dequeue());
                        }
                    }
                    swDownloadLog.WriteLine(thisLog);
                }
            }
            catch (System.Exception)
            {
                lock (outputBuffer)
                {
                    outputBuffer.Enqueue(thisLog);
                }
            }
        }

        private static void writeErrorLog(uint downloadID, Queue<string> outputBuffer, DownloadedStatus.Status status)
        {
            string errorLogFile = imageDir + "Error" + ".log";
            uint imageID = downloadID;
            string imageUrl = invertedImageUrlList[imageID];
            string shortImagePath = imagePathList[imageID];
            string thisLog = imageID + "\t" + status + "\t" + imageUrl + "\t" + shortImagePath;

            try
            {
                using (StreamWriter swErrorLog = new StreamWriter(errorLogFile, true))
                {
                    if (outputBuffer.Count != 0)
                    {
                        lock (outputBuffer)
                        {
                            while (outputBuffer.Count != 0)
                                swErrorLog.WriteLine(outputBuffer.Dequeue());
                        }
                    }
                    swErrorLog.WriteLine(thisLog);
                }
            }
            catch (System.Exception)
            {
                lock (outputBuffer)
                {
                    outputBuffer.Enqueue(thisLog);
                }
            }
        }
        #endregion

        #region Discarded Code
        //private static void DownloadRemoteImageFile(string imageUrl, string fileName)
        //{
        //    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(imageUrl);
        //    HttpWebResponse response = (HttpWebResponse)request.GetResponse();

        //    // Check if the remote file is found. The ContentType
        //    // check is performed since a request for a non-existent
        //    // image file might be redirected to a 404-page, which would
        //    // yield the StatusCode "OK", even though the image is not
        //    // found.
        //    if ((response.StatusCode == HttpStatusCode.OK ||
        //            response.StatusCode == HttpStatusCode.Moved ||
        //            response.StatusCode == HttpStatusCode.Redirect) &&
        //            response.ContentType.StartsWith("image", StringComparison.OrdinalIgnoreCase))
        //    {

        //        // if the remote file is found, download it
        //        using (Stream inputStream = response.GetResponseStream())
        //        using (Stream outputStream = File.OpenWrite(fileName))
        //        {
        //            byte[] buffer = new byte[4096];
        //            int bytesRead;
        //            do
        //            {
        //                bytesRead = inputStream.Read(buffer, 0, buffer.Length);
        //                outputStream.Write(buffer, 0, bytesRead);
        //            }
        //            while (bytesRead != 0);
        //        }
        //    }
        //}
        #endregion

        static void Main(string[] args)
        {
            Console.WriteLine(new string('*', 79));
            Console.WriteLine();

            try
            {
                SetConsoleCtrlHandler(new HandlerRoutine(ConsoleCtrlCheck), true);          // to avoid unintentionally close the console
                ReadConfigFile();
            }
            catch (System.Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Fatal ERR: " + ex.Message);
                Console.ResetColor();
                Environment.Exit(-1);
            }

            printParameterInfo();

            try
            {
                ReadUrlListFile();
                if (!isValidationOnly)
                {
                    ReadDownloadLogFile();
                    MakeDownloadList();
                    SafetyCheckAndInit();
                    WriteParameterFile();
                    DoDownload();
                }
                if (!isFastValidation)
                    ValidateDownloading();
                else
                    ValidateDownloading_Fast();

                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
            catch (System.Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Fatal ERR: " + ex.Message);
                Console.WriteLine("   - Please refer to the runtime log for details.");
                Console.ResetColor();
                string runtimeLogFile = imageDir + "Runtime" + ".log";
                Directory.CreateDirectory(imageDir);
                using (StreamWriter swRuntimeLog = new StreamWriter(runtimeLogFile))
                {
                    swRuntimeLog.WriteLine("[{0}] Fatal error:", DateTime.Now);
                    swRuntimeLog.WriteLine("   - Error Message: " + ex.Message);
                    swRuntimeLog.WriteLine("   - Target: " + ex.TargetSite);
                    swRuntimeLog.WriteLine();
                    swRuntimeLog.WriteLine("--- Trace ---");
                    swRuntimeLog.WriteLine(ex.StackTrace);
                }
                Environment.Exit(-2);
            }
        }

        private static void printParameterInfo()
        {
            Console.WriteLine("< Parameters >");
            if (isDownloadAllImages)
                Console.WriteLine("ImageRecordRange= all");
            else
                Console.WriteLine("ImageRecordRange= {0}-{1}", minImageIDForDownloading, maxImageIDForDownloading);
            Console.WriteLine("ConcurrentThreads= " + nConcurrentThreads);
            Console.WriteLine("ValidationThreads= " + nValidationThreads);
            Console.WriteLine("ThreadSurvivingTime= {0}ms", survivingTimeThreshold);
            Console.WriteLine("ImagesInOneFolder= " + nImagesInOneFolder);
            Console.WriteLine("IsForceNewDownload= " + isForceNewDownload);
            Console.WriteLine("IsUseImageIDInUrlList= " + isUseImageIDInUrlList);
            Console.WriteLine("IsTryFailedDownload= " + isTryFailedDownload);
            Console.WriteLine("IsFastValidation= " + isFastValidation);
            Console.WriteLine("IsValidationOnly= " + isValidationOnly);
            Console.WriteLine();
            //Console.WriteLine("Processing...");
        }

        #region ConsoleExitInterception
        private static bool ConsoleCtrlCheck(CtrlTypes ctrlType)
        {
            // Put your own handler here
            string runtimeLogFile = imageDir + "Runtime" + ".log";
            switch (ctrlType)
            {
                case CtrlTypes.CTRL_C_EVENT:
                    break;

                case CtrlTypes.CTRL_BREAK_EVENT:
                    break;

                case CtrlTypes.CTRL_CLOSE_EVENT:
                    // Warning: Process is to be killed, code is not secured here.
                    Directory.CreateDirectory(imageDir);
                    using (StreamWriter swRuntimeLog = new StreamWriter(runtimeLogFile))
                    {
                        swRuntimeLog.WriteLine("[{0}] Process error:", DateTime.Now);
                        swRuntimeLog.WriteLine("   - Process is unintentionally killed.");
                    }
                    break;

                case CtrlTypes.CTRL_LOGOFF_EVENT:
                case CtrlTypes.CTRL_SHUTDOWN_EVENT:
                    // Warning: Process is to be killed, code is not secured here.
                    Directory.CreateDirectory(imageDir);
                    using (StreamWriter swRuntimeLog = new StreamWriter(runtimeLogFile))
                    {
                        swRuntimeLog.WriteLine("[{0}] Process error:", DateTime.Now);
                        swRuntimeLog.WriteLine("   - Process is killed due to user logoff.");
                    }
                    break;
            }
            return true;
        }

        // Declare the SetConsoleCtrlHandler function
        // as external and receiving a delegate.
        [DllImport("Kernel32")]
        public static extern bool SetConsoleCtrlHandler(HandlerRoutine Handler, bool Add);

        // A delegate type to be used as the handler routine
        // for SetConsoleCtrlHandler.
        public delegate bool HandlerRoutine(CtrlTypes CtrlType);

        // An enumerated type for the control messages
        // sent to the handler routine.
        public enum CtrlTypes
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT,
            CTRL_CLOSE_EVENT,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT
        }
        #endregion

        // Inputs
        private static string configFile = "config.ini";
        private static string imageUrlListFile = null;
        // Outputs
        private static string imageDir = null;
        // Parameters
        private static bool isDownloadAllImages = false;
        private static bool isUseImageIDInUrlList = false;
        private static bool isForceNewDownload = false;
        private static bool isTryFailedDownload = false;
        private static bool isFastValidation = false;
        private static bool isValidationOnly = false;
        private static uint minImageIDForDownloading = 0;
        private static uint maxImageIDForDownloading = 0;
        private static int nConcurrentThreads = 50;
        private static int nValidationThreads = 4;
        private static long survivingTimeThreshold = 30000;
        private static int nImagesInOneFolder = 10000;

        // Data
        private static Dictionary<string, uint> imageUrlList = null;
        private static Dictionary<uint, string> invertedImageUrlList = null;
        private static Dictionary<uint, DownloadedStatus.Status> imageDownloadStatus = null;
        private static Queue<uint> downloadImageIDList = null;
        private static Dictionary<uint, string> imagePathList = null;
        private static int downloadingThreadCount = 0;
        private static uint nSuccessfulDownloads = 0;
        private static uint nGeneralErrorDownloads = 0;
        private static uint nTimeoutDownloads = 0;
        private static Dictionary<WebClient, Timer> killerList = null;      // used to protect from garbage collecting, GREAT TRICK!
        private static Queue<WebClient> webClientRemovalBuffer_for_killerList = null;       // used to enhance the safety of concurrency
        // Cache
        private static Queue<string> downloadLogOutputBuffer = null;
        private static Queue<string> errorLogOutputBuffer = null;

        public struct DownloadLogItem
        {
            public uint ImageID;
            public DownloadedStatus.Status DownloadStatus;
            public string ImageUrl;
            public string FilePath;
        }

        public enum ImageType
        {
            bmp, emf, exif, gif, ico, jpg, png, tiff, wmf, image, _null
        }
    }

    static class DownloadedStatus
    {
        public enum Status
        {
            Success,
            FileNotExist,
            InvalidUrl,
            TimeOut,
            VFailed,            // verification failure
            GeneralError
        }

        public static Status Parse(string str)
        {
            str = str.Trim();
            switch (str)
            {
                case "Success":
                    return Status.Success;
                case "FileNotExist":
                    return Status.FileNotExist;
                case "InvalidUrl":
                    return Status.InvalidUrl;
                case "TimeOut":
                    return Status.TimeOut;
                case "VFailed":
                    return Status.VFailed;
                case "GeneralError":
                    return Status.GeneralError;
                default:
                    return Status.GeneralError;
            }
        }
    }

    class WebclientDownloadIDType
    {
        public WebClient Webclient;
        public uint DownloadID;

        public WebclientDownloadIDType(WebClient wc, uint dID)
        {
            Webclient = wc;
            DownloadID = dID;
        }
    }
}
