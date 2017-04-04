using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using System.Reactive.Linq;
using System.Reactive.Windows.Foundation;
using System.Reactive;
using Windows.Networking.Connectivity;
using System.Reactive.Disposables;
using System.Net;
using System.Reactive.Subjects;
using Microsoft.Azure.Devices.Common.Security;

// 空白ページのアイテム テンプレートについては、http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409 を参照してください

namespace IoTHubDevice
{
    /// <summary>
    /// それ自体で使用できる空白ページまたはフレーム内に移動できる空白ページ。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private string key = "primary-key_or_secondary_key";
        private string keyname = "access_policy_name";
        private string hostname = "~~~~.azure-devices.net";
        private Guid guid = Guid.NewGuid();
        private string deviceId;
        private string fileName = "image.jpg";
        private string correlationId;
        private Uri fileUri;
        private HttpStatusCode statusCode;

        private string createSignature()
            => new SharedAccessSignatureBuilder()
            {
                Key = key,
                KeyName = keyname,
                Target = hostname,
                TimeToLive = TimeSpan.FromDays(1)
            }.ToSignature();

        /// <summary>
        /// {{
        ///   "deviceId": "7a9ee38f-9e28-45a2-9033-181317329d08",
        ///   "generationId": "636267892862588915",
        ///   "etag": "MA==",
        ///   "connectionState": "Disconnected",
        ///   "status": "enabled",
        ///   "statusReason": null,
        ///   "connectionStateUpdatedTime": "0001-01-01T00:00:00",
        ///   "statusUpdatedTime": "0001-01-01T00:00:00",
        ///   "lastActivityTime": "0001-01-01T00:00:00",
        ///   "cloudToDeviceMessageCount": 0,
        ///   "authentication": {
        ///     "symmetricKey": {
        ///       "primaryKey": "~~~~~~~",
        ///       "secondaryKey": "~~~~~~~~"
        ///     },
        ///     "x509Thumbprint": {
        ///       "primaryThumbprint": null,
        ///       "secondaryThumbprint": null
        ///     }
        ///   }
        /// }}
        /// </summary>
        public class responseRegister
        {
            public string deviceId { get; set; }
        }

        /// <summary>
        /// {
        ///     "correlationId": "",
        ///     "hostName": "",
        ///     "containerName": "",
        ///     "blobName": "",
        ///     "sasToken": ""
        /// }
        public class responseGetStorageSasUri
        {
            public string correlationId { get; set; }
            public string hostName { get; set; }
            public string containerName { get; set; }
            public string blobName { get; set; }
            public string sasToken { get; set; }
        }

        public MainPage()
        {
            this.InitializeComponent();

            Observable.FromEventPattern<RoutedEventHandler, RoutedEventArgs>(
                h => this.Loaded += h,
                h => this.Loaded -= h
                ).ObserveOnDispatcher()
                .FirstAsync()
                .Subscribe(_ => {

                    var trigger = new Subject<object>();
                    Observable.Merge(trigger,
                        Observable.FromEvent<NetworkStatusChangedEventHandler, object>(
                            h => new NetworkStatusChangedEventHandler(target => h(target)),
                            h => NetworkInformation.NetworkStatusChanged += h,
                            h => NetworkInformation.NetworkStatusChanged -= h)
                        ).FirstAsync(__ => (NetworkConnectivityLevel.InternetAccess.Equals(NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel())))
                        .ObserveOnDispatcher()
                        .Subscribe(async __ => {

                            await registerAsync();

                            Observable.Interval(TimeSpan.FromMinutes(1)).Subscribe(async ___ => await updateTwinAsync());

                            capture();
                        });

                    trigger.OnNext(null);
                });
        }

        /// <summary>
        /// 画像取得.
        /// </summary>
        private void capture()
        {
            MediaCapture mediaCaptureManager = new MediaCapture();
            mediaCaptureManager.InitializeAsync()
                .ToObservable()
                .ObserveOnDispatcher()
                .SelectMany(_ =>
                {
                    previewElement.Source = mediaCaptureManager;
                    return mediaCaptureManager.StartPreviewAsync().ToObservable();
                })
                .Subscribe(_ =>
                {
                    Windows.Storage.KnownFolders.PicturesLibrary.CreateFileAsync("tmp.jpg", Windows.Storage.CreationCollisionOption.ReplaceExisting)
                        .ToObservable()
                        .Repeat()
                        .Delay(TimeSpan.FromSeconds(10))
                        .Subscribe(photoStorageFile =>
                        {
                            Task.Run(async () => {

                                await mediaCaptureManager.CapturePhotoToStorageFileAsync(ImageEncodingProperties.CreateJpeg(), photoStorageFile);

                                await getStorageSasUriAsync();

                                await putStorageFileAsync(photoStorageFile);

                                await notificationFileUploadAsync();
                            }).Wait();
                        }, ex => Debug.WriteLine(ex.Message));
                });
        }

        /// <summary>
        /// Device登録.
        /// </summary>
        /// <returns></returns>
        private async Task registerAsync()
        {
            var httpClient = new System.Net.Http.HttpClient();
            httpClient.DefaultRequestHeaders.Accept.Clear();

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", createSignature());

            var stringPayload = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                deviceId = guid.ToString()
            });

            var httpContent = new System.Net.Http.StringContent(stringPayload, System.Text.Encoding.UTF8, "application/json");

            var res = await httpClient.PutAsync($"https://{hostname}/devices/{guid.ToString()}?api-version=2016-11-14", httpContent);

            if (HttpStatusCode.OK.Equals(res?.StatusCode))
            {
                var str = await res.Content.ReadAsStringAsync();
                var ret = Newtonsoft.Json.JsonConvert.DeserializeObject<responseRegister>(str);

                deviceId = ret.deviceId;
            }
        }

        /// <summary>
        /// デバイス死活監視.
        /// </summary>
        /// <returns></returns>
        private async Task updateTwinAsync()
        {
            var httpClient = new System.Net.Http.HttpClient();
            httpClient.DefaultRequestHeaders.Accept.Clear();

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", createSignature());

            var stringPayload = "{ 'properties': { 'desired': { '$metadata': { '$lastUpdated': '" + DateTime.UtcNow.ToString("s") + "Z" + "' } } } }";

            var httpContent = new System.Net.Http.StringContent(stringPayload, System.Text.Encoding.UTF8, "application/json");

            var res = await httpClient.SendAsync(new HttpRequestMessage(new HttpMethod("PATCH"), $"https://{hostname}/twins/{guid.ToString()}?api-version=2016-11-14") { Content = httpContent });

            if (HttpStatusCode.OK.Equals(res?.StatusCode))
            { }
        }

        /// <summary>
        /// BlobのSasToken取得.
        /// </summary>
        /// <returns></returns>
        private async Task getStorageSasUriAsync()
        {
            var httpClient = new System.Net.Http.HttpClient();
            httpClient.DefaultRequestHeaders.Accept.Clear();

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", createSignature());

            var stringPayload = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                blobName = fileName
            });

            var httpContent = new System.Net.Http.StringContent(stringPayload, System.Text.Encoding.UTF8, "application/json");

            var res = await httpClient.PostAsync($"https://{hostname}/devices/{guid.ToString()}/files?api-version=2016-11-14", httpContent);

            if (HttpStatusCode.OK.Equals(res?.StatusCode))
            {
                var str = await res.Content.ReadAsStringAsync();
                var ret = Newtonsoft.Json.JsonConvert.DeserializeObject<responseGetStorageSasUri>(str);

                correlationId = ret.correlationId;
                fileUri = new Uri($"https://{ret.hostName}/{ret.containerName}/{ret.blobName}{ret.sasToken}");
            }
        }

        /// <summary>
        /// ファイルのアップロード.
        /// </summary>
        /// <param name="file"></param>
        /// <returns></returns>
        private async Task putStorageFileAsync(StorageFile file)
        {
            using (var stream = await file.OpenStreamForReadAsync())
            {
                var bytes = new byte[(int)stream.Length];
                stream.Read(bytes, 0, (int)stream.Length);

                var httpContent = new ByteArrayContent(bytes);

                var httpClient = new System.Net.Http.HttpClient();
                httpClient.DefaultRequestHeaders.Accept.Clear();

                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-ms-version", "2016-05-31");
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-ms-date", DateTime.UtcNow.ToString());
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");

                var res = await httpClient.PutAsync(fileUri, httpContent);

                statusCode = res.StatusCode;
            }
        }

        /// <summary>
        /// アップロード完了通知.
        /// </summary>
        /// <returns></returns>
        private async Task notificationFileUploadAsync()
        {
            var httpClient = new System.Net.Http.HttpClient();
            httpClient.DefaultRequestHeaders.Accept.Clear();

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", createSignature());

            var stringPayload = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                correlationId = correlationId,
                isSuccess =  HttpStatusCode.Created.Equals(statusCode),
                statusCode = (int)statusCode,
                statusDescription = Enum.GetName(typeof(HttpStatusCode), statusCode),
            });

            var httpContent = new System.Net.Http.StringContent(stringPayload, System.Text.Encoding.UTF8, "application/json");

            var res = await httpClient.PostAsync($"https://{hostname}/devices/{guid.ToString()}/files/notifications?api-version=2016-11-14", httpContent);

            if (HttpStatusCode.NoContent.Equals(res?.StatusCode))
            { }
        }
    }
}
