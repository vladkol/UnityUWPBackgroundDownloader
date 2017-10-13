using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class DownloadFileRunner : MonoBehaviour {

    public string fileUrl = string.Empty;
    public string localPath = string.Empty;
    public UWPDownloadManager downloadManager;
    public RectTransform listContent;
    public GameObject downloadItemPrefab;

    // Use this for initialization
    void Start ()
    {
        if (string.IsNullOrEmpty(fileUrl))
            return;

        if (downloadManager == null)
            downloadManager = GetComponent<UWPDownloadManager>();

        if (downloadManager == null)
            return;

        var uriDownload = new System.Uri(fileUrl);
        UWPDownloader downloader = downloadManager.GetDownloader();
        bool bExists = false;

        foreach(var uri in downloader.GetDownloads())
        {
            var go = (GameObject)Instantiate(downloadItemPrefab, listContent.transform);
            Text item = go.GetComponent<Text>();

            item.text = uri.ToString();

            downloader.DownloadUri(uri, string.Empty, 
                (System.Uri itemUri, ulong downloadedBytes, ulong bytesToDownload) =>
                {
                    ShowProgress(item, itemUri, downloadedBytes, bytesToDownload);
                },

                (System.Uri itemUri, string localPath, bool success) =>
                {
                    ReportDownload(item, itemUri, localPath, success);
                });

            if (uri.ToString() == fileUrl)
                bExists = true;
        }

        
        if(!bExists)
        {
            var size = UWPDownloader.GetDownloadSize(uriDownload);
            System.UInt64 freeSpace = int.MaxValue;

#if UNITY_WSA && ENABLE_WINMD_SUPPORT
            freeSpace = UWPDownloader.GetDriveFreeSpace(Windows.Storage.ApplicationData.Current.LocalCacheFolder);
#endif

            Debug.LogFormat("Downloading {0} bytes with {1} bytes of drive space available.", size, freeSpace);

            var go = (GameObject)Instantiate(downloadItemPrefab, listContent.transform);
            Text item = go.GetComponent<Text>();

            item.text = fileUrl;

            downloader.DownloadUri(uriDownload, localPath,

                (System.Uri uri, ulong downloadedBytes, ulong bytesToDownload) =>
                {
                    ShowProgress(item, uri, downloadedBytes, bytesToDownload);
                },

                (System.Uri uri, string localPath, bool success) =>
                {
                    ReportDownload(item, uri, localPath, success);
                });
        }
    }

    void ShowProgress(Text item, System.Uri itemUri, ulong downloadedBytes, ulong bytesToDownload)
    {
        item.text = string.Format("{0}% - {1}", downloadedBytes * 100 / bytesToDownload, itemUri.ToString());
    }

    void ReportDownload(Text item, System.Uri uri, string localPath, bool success)
    {
        if (success)
        {
            item.text = string.Format("Done: {0} {1}", uri, localPath);
        }
        else
        {
            item.text = string.Format("Failed: {0}", uri);
        }
    }

    
}
