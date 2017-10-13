using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class UWPDownloadManager : MonoBehaviour
{
    public string downloaderID = string.Empty;

    private UWPDownloader downloader = null;

    private void Awake()
    {
        UWPDownloader.Initialize(downloaderID);
        downloader = UWPDownloader.Instance;
    }

    public UWPDownloader GetDownloader()
    {
        return downloader;
    }
}
