using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Threading;
using System.IO;
using System.Linq;

#if UNITY_WSA && ENABLE_WINMD_SUPPORT
    using System.Threading.Tasks;
    using Windows.Networking.BackgroundTransfer;
    using Windows.Storage;
#endif

#if UNITY_WSA && ENABLE_WINMD_SUPPORT
    using Windows.Web.Http;
#endif

public class UWPDownloader
{
    private static UWPDownloader _downloaderSingleton;

    public enum DownloadStatus
    {
        NotStarted = 0,
        Completed,
        Downloading, 
        Paused, 
        Canceled, 
        Failed
    }

#if UNITY_WSA && ENABLE_WINMD_SUPPORT
    private class Operation
    {
        private DownloadOperation _operation;

        public DownloadOperation operation
        {
            get
            {
                return _operation;
            }

            set
            {
                _operation = value;
            }
        }

        public event Action<Uri, ulong, ulong> progress;
        public event Action<Uri, string, bool> complete;

        public void FireComplete(Uri uri, string path, bool success)
        {
            if(complete != null)
            {
                try
                {
                    complete(uri, path, success);
                }
                catch { }
            }
        }

        public void FireProgress(Uri uri, ulong bytesDownloaded, ulong totalBytesToDownload)
        {
            if (progress != null)
            {
                try
                {
                    progress(uri, bytesDownloaded, totalBytesToDownload);
                }
                catch { }
            }
        }
    }
#endif

    public static UWPDownloader Instance
    {
        get
        {
            return _downloaderSingleton;
        }
    }

#if UNITY_WSA && ENABLE_WINMD_SUPPORT
    private BackgroundDownloader _downloader;
    private BackgroundTransferGroup _group;

    private Dictionary<Uri, Operation> _operations = new Dictionary<Uri, Operation>();
    private Semaphore _sync = new Semaphore(1, 1);
#endif

    private string downloaderID;

    public static void Initialize(string downloaderID)
    {
        if (UnityEngine.WSA.Application.RunningOnAppThread())
        {
            _downloaderSingleton = new UWPDownloader(downloaderID);
        }
        else
        {
            UnityEngine.WSA.Application.InvokeOnAppThread(() =>
            {
                _downloaderSingleton = new UWPDownloader(downloaderID);
            }, true);
        }
    }

    private UWPDownloader(string downloaderID)
    {
#if UNITY_WSA && ENABLE_WINMD_SUPPORT
        this.downloaderID = downloaderID;
        Construct();
#endif
    }

#if UNITY_WSA && ENABLE_WINMD_SUPPORT

    private void Construct()
    {
        if(string.IsNullOrEmpty(downloaderID))
        {
            downloaderID = Windows.ApplicationModel.Package.Current.Id.FullName + "_downloads";
        }

        _group = BackgroundTransferGroup.CreateGroup(downloaderID.Substring(0, 40));
        _group.TransferBehavior = BackgroundTransferBehavior.Serialized;
        _downloader = new BackgroundDownloader();
        _downloader.TransferGroup = _group;

        InitializeOperations().Wait();
    }

    private async Task InitializeOperations()
    {
        try
        {
            _sync.WaitOne();
            IReadOnlyList<DownloadOperation> list = await BackgroundDownloader.GetCurrentDownloadsForTransferGroupAsync(_group).AsTask();

            foreach (var operation in list)
            {
                Operation oper = new Operation();
                oper.operation = operation;

                _operations.Add(operation.RequestedUri, oper);
            }

            foreach (var oper in _operations.Values)
            {
                if (oper.operation.Progress.Status != BackgroundTransferStatus.Completed)
                {
                    StartDownload(oper, true, true);
                }
            }

        }
        finally
        {
            _sync.Release();
        }
    }

    private void StartDownload(Operation operationToStart, bool attachToExisting, bool handleCompleteness)
    {
        DownloadOperation operation = operationToStart.operation;

        if (operation.Progress.Status == BackgroundTransferStatus.Canceled || operation.Progress.Status == BackgroundTransferStatus.Error)
        {
            if (_operations.ContainsKey(operation.RequestedUri))
                _operations.Remove(operation.RequestedUri);

            if (operation.ResultFile != null)
            {
                try
                {
#pragma warning disable 4014
                    operation.ResultFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
#pragma warning restore 4014
                }
                catch { }
            }

            Task.Run(() =>
            {
                UnityEngine.WSA.Application.InvokeOnAppThread(() =>
                {
                    try
                    {
                        operationToStart.FireComplete(operation.RequestedUri, string.Empty, false);
                    }
                    catch { }
                }, false);
            });

            return;
        }

        operation.RangesDownloaded += (DownloadOperation sender, BackgroundTransferRangesDownloadedEventArgs args) =>
        {
            UnityEngine.WSA.Application.InvokeOnAppThread(() =>
            {
                if (_operations.ContainsKey(sender.RequestedUri))
                {
                    try
                    {
                        _operations[sender.RequestedUri].FireProgress(sender.RequestedUri, sender.Progress.BytesReceived, sender.Progress.TotalBytesToReceive);
                    }
                    catch { }
                }
            }, false);
        };

        var initialStatus = operation.Progress.Status;
        if (initialStatus == BackgroundTransferStatus.PausedByApplication ||
            initialStatus == BackgroundTransferStatus.PausedRecoverableWebErrorStatus)
        {
            operation.Resume();
        }

        bool bAttach = attachToExisting ||
                        (initialStatus == BackgroundTransferStatus.Running); 

        Task<DownloadOperation> task = bAttach ? operation.AttachAsync().AsTask() : operation.StartAsync().AsTask();

        if(handleCompleteness)
        {
            task.ContinueWith(async (Task<DownloadOperation> resOperation) =>
            {
                if (!attachToExisting && _operations.ContainsKey(operation.RequestedUri))
                {
                    _operations.Remove(operation.RequestedUri);
                }

                var status = resOperation.Result.Progress.Status;

                bool bRes = resOperation.Status == TaskStatus.RanToCompletion &&
                    (status == BackgroundTransferStatus.Completed ||
                        (status == BackgroundTransferStatus.Idle && operation.Progress.BytesReceived == operation.Progress.TotalBytesToReceive));

                if (resOperation.Status == TaskStatus.Faulted || resOperation.Status == TaskStatus.Canceled
                        || resOperation.Result.Progress.Status == BackgroundTransferStatus.Error || resOperation.Result.Progress.Status == BackgroundTransferStatus.Canceled)
                {
                    if (operation.ResultFile != null)
                    {
                        try
                        {
                            await operation.ResultFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        }
                        catch { }
                    }
                }

#pragma warning disable 4014
                Task.Run(() =>
                {
                    UnityEngine.WSA.Application.InvokeOnAppThread(() =>
                    {
                        try
                        {
                            operationToStart.FireComplete(operation.RequestedUri, operation.ResultFile.Path, bRes);
                        }
                        catch { }
                    }, false);
                });
#pragma warning restore 4014

            });
        }

    }

#endif

    public void DownloadUri(Uri uri, string localPath, Action<Uri, ulong, ulong> progressHandler, Action<Uri, string, bool> completeHandler)
    {
#if UNITY_WSA && ENABLE_WINMD_SUPPORT
        try
        {
            var path = localPath;

            _sync.WaitOne();

            if (!string.IsNullOrEmpty(path) && !path.StartsWith("file-access://"))
            {
                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, path);
                }
            }

            Operation oper = null;
            bool needNewOperation = false;

            if (_operations.ContainsKey(uri))
            {
                oper = _operations[uri];
                
                if(oper == null)
                {
                    needNewOperation = true;
                }
                else if (oper.operation.Progress.Status == BackgroundTransferStatus.Completed)
                {
                    _operations.Remove(uri);

                    Task.Run(() =>
                    {
                        UnityEngine.WSA.Application.InvokeOnAppThread(() =>
                        {
                            try
                            {
                                progressHandler(uri, oper.operation.Progress.TotalBytesToReceive, oper.operation.Progress.TotalBytesToReceive);
                            }
                            catch { }
                            try
                            {
                                completeHandler(uri, oper.operation.ResultFile.Path, true);
                            }
                            catch { }
                        }, false);
                    });

                    return;
                } 
                else if(oper.operation.Progress.Status == BackgroundTransferStatus.Error || oper.operation.Progress.Status == BackgroundTransferStatus.Canceled)
                {
                    needNewOperation = true;
                }

                if (needNewOperation)
                {
                    _operations.Remove(uri);
                    oper = null;
                }
            }
            else
            {
                needNewOperation = true;
            }

            Task<StorageFile> fileTask = null;

            if (needNewOperation)
            {
                if (path.StartsWith("file-access://"))
                {
                    var token = path.Substring("file-access://".Length).TrimStart('/');
                    fileTask = Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList.GetFileAsync(token).AsTask();
                }
                else
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    var stream = File.Create(path);
#if !ENABLE_IL2CPP
                    stream.Dispose();
#else
                    stream = null;
#endif
                    fileTask = StorageFile.GetFileFromPathAsync(path).AsTask();
                }
            }
            else
            {
                fileTask = Task.Run<StorageFile>(() =>
                {
                    return (StorageFile)oper.operation.ResultFile;
                });
            }

            fileTask.ContinueWith((Task<StorageFile> task) =>
            {
                if(task.IsFaulted || task.IsCanceled)
                {
                    if(completeHandler != null)
                    {
                        Task.Run(() =>
                        {
                            UnityEngine.WSA.Application.InvokeOnAppThread(() =>
                            {
                                try
                                {
                                    completeHandler(uri, string.Empty, false);
                                }
                                catch { }
                            }, false);
                        });
                    }

                    return;
                }

                _sync.WaitOne();
                try
                {
                    var file = task.Result;
                    DownloadOperation operation = null;

                    if (oper == null)
                        needNewOperation = true;

                    if (needNewOperation)
                    {
                        oper = new Operation();

                        operation = _downloader.CreateDownload(uri, file);
                        oper.operation = operation;

                        _operations.Add(operation.RequestedUri, oper);
                    }
                    else
                    {
                        operation = oper.operation;
                    }

                    if (completeHandler != null)
                        _operations[uri].complete += completeHandler;
                    if (progressHandler != null)
                        _operations[uri].progress += progressHandler;

                    StartDownload(oper, false, needNewOperation);
                }
                finally
                {
                    _sync.Release();
                }
            });
        }
        finally
        {
            _sync.Release();
        }
#endif
    }

    public bool CancelDownloading(Uri uri)
    {
        bool bRes = false;
#if UNITY_WSA && ENABLE_WINMD_SUPPORT
        if(_operations.ContainsKey(uri))
        {
            bRes = true;

            DownloadOperation oper = _operations[uri].operation;
            IStorageFile file = oper.ResultFile;

            CancellationTokenSource canceledToken = new CancellationTokenSource();
            canceledToken.Cancel();

            oper.AttachAsync().AsTask(canceledToken.Token).ContinueWith((task) =>
            {
                if (file != null)
                {
                    try
                    {
                        file.DeleteAsync();
                    }
                    catch { }
                }
            });

            _operations.Remove(uri);
        }
#endif
        return bRes;
    }

    public List<Uri> GetDownloads()
    {
        List<Uri> list = new List<Uri>();
#if UNITY_WSA && ENABLE_WINMD_SUPPORT
        foreach(var k in _operations.Keys)
        {
            list.Add(k);
        }
#endif
        return list;
    }


    public string GetDownloadedLocalFile(Uri uri)
    {
#if UNITY_WSA && ENABLE_WINMD_SUPPORT
        if (_operations.ContainsKey(uri))
        {
            var oper = _operations[uri];
            return oper.operation.ResultFile.Path;
        }
#endif
        return string.Empty;
    }

    public DownloadStatus GetDownloadStatus(Uri uri)
    {
        DownloadStatus res = DownloadStatus.NotStarted;
#if UNITY_WSA && ENABLE_WINMD_SUPPORT
        if (_operations.ContainsKey(uri))
        {
            DownloadOperation oper = _operations[uri].operation;
            switch(oper.Progress.Status)
            {
                case BackgroundTransferStatus.Canceled:
                    res = DownloadStatus.Canceled;
                    break;
                case BackgroundTransferStatus.Completed:
                    res = DownloadStatus.Completed;
                    break;
                case BackgroundTransferStatus.Running:
                    res = DownloadStatus.Downloading;
                    break;
                case BackgroundTransferStatus.Error:
                    res = DownloadStatus.Failed;
                    break;
                default:
                    res = DownloadStatus.Paused;
                    break;
            }
        }
#endif
        return res;
    }

#if UNITY_WSA
    #if ENABLE_IL2CPP
        [System.Runtime.InteropServices.DllImport("__Internal", ExactSpelling = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    #else
        [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    #endif
        static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);
#endif

#if UNITY_WSA && ENABLE_WINMD_SUPPORT
    public static UInt64 GetDriveFreeSpace(StorageFolder folder)
    {
        ulong uFree = 0, uTotal = 0, uTotalFree = 0;

        GetDiskFreeSpaceExW(folder.Path, out uFree, out uTotal, out uTotalFree);
        return (UInt64)uFree;
    }
#endif

    public static UInt64 GetDownloadSize(Uri uri)
    {
        UInt64 result = 0;

        try
        {
            ManualResetEvent requestEvent = new ManualResetEvent(false);

#if UNITY_WSA && ENABLE_WINMD_SUPPORT
            HttpClient client = new HttpClient();
            HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Head, uri);
            HttpResponseMessage response = null;

            var task = client.SendRequestAsync(msg);
            task.Completed = 
                (Windows.Foundation.IAsyncOperationWithProgress<Windows.Web.Http.HttpResponseMessage, Windows.Web.Http.HttpProgress > asyncInfo, Windows.Foundation.AsyncStatus asyncStatus) =>
            {
                if(asyncInfo.Status == Windows.Foundation.AsyncStatus.Completed)
                {
                    response = asyncInfo.GetResults();
                }
                if (asyncStatus != Windows.Foundation.AsyncStatus.Started)
                {
                    requestEvent.Set();
                }
            };
            requestEvent.WaitOne();
#else
            System.Net.HttpWebRequest msg = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(uri);
            msg.Method = "HEAD";
            System.Net.WebResponse response = null;

            try
            {
                response = msg.GetResponse();
            }
            catch { }
#endif

            if (response != null)
            {
#if UNITY_WSA && ENABLE_WINMD_SUPPORT
                if (response.IsSuccessStatusCode && response.Content != null)
                {
                    var cl = response.Content.Headers.ContentLength;
                    if (cl.HasValue)
                        result = (UInt64)cl.Value;
                }
#else
                result = (UInt64)response.ContentLength;
#endif
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogErrorFormat("Exception getting download size: {0} \n{1}", ex.Message, ex.StackTrace);
        }
        return result;
    }
}
