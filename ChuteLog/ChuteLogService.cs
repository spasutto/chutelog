using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using System.Threading;
using Android.Util;
using System.IO;
using Java.Util;
using Android.Bluetooth;
using Android.Locations;

namespace ChuteLog
{
	//TODO : au bout de 20 minutes le service s'arrête?
	[Service(Exported = true, Name = "net.pasutto.ChuteLogService")]
	public class ChuteLogService : Service, ILocationListener
	{
		private static String TAG = "X:" + typeof(ChuteLogService).Name;
		ChuteLogServiceBinder binder;
		bool recording = false, okbluetooth = false, okgps = false, initended = false;

		private const int FREQ_BT_ALT = 5;
		private const int FREQ_LOGGING = 5;
		private const int AUTO_STOP_TIMER = 45 * 60 * 1000;// 45 minutes
		private int periode_bt_alt = Math.Max(25, (int)Math.Round(1000.0f / ((double)FREQ_BT_ALT)));
		private int periode_freq_logging = Math.Max(25, (int)Math.Round(1000.0f / ((double)FREQ_LOGGING)));
		//private const string DEVICE_ADDRESS = "20:13:10:15:33:66";//HC-05
		private const string DEVICE_ADDRESS = "98:d3:31:90:2d:0e";//HC-06
		private UUID PORT_UUID = UUID.FromString("00001101-0000-1000-8000-00805f9b34fb");//Serial Port Service ID
		BluetoothAdapter bluetoothAdapter;
		BluetoothDevice device;
		BluetoothSocket btsocket;
		Stream inputStream, outputStream;
		LocationManager _locationManager;
		string _locationProvider;
		double freq_gps = 0.0f;

		struct LogPoint
		{
			public double lat, lon, alt, millis;
		}

		List<LogPoint> points = new List<LogPoint>();
		DateTime datePrevLoc = DateTime.Now, dtStartLogging;
		LogPoint currentpos;

		public bool IsInit { get { return initended; } }
		public bool IsRecording { get { return recording; } }
		public bool IsBluetoothOK { get { return okbluetooth; } }
		public bool IsGPSOK { get { return okgps; } }
		public double CurrentAlt { get { return currentpos.alt; } }
		public double FreqGPS { get { return freq_gps; } }

		#region GESTION SERVICE
		public enum ServiceStatusChangedType
		{
			INIT_ENDED,
			ERROR,
			LOCATION_CHANGED
		}
		public delegate void ServiceStatusChangedEventHandler(object sender, ServiceStatusChangedType status, string message = "");

		public class ChuteLogServiceBinder : Binder
		{
			public event ServiceStatusChangedEventHandler ServiceStatusChanged;
			ChuteLogService service = null;
			public ChuteLogService Service { get { return service; } }
			public bool IsInit { get { return service.IsInit; } }
			public bool IsRecording { get { return service.IsRecording; } }
			public bool IsBluetoothOK { get { return service.IsBluetoothOK; } }
			public bool IsGPSOK { get { return service.IsGPSOK; } }
			public double CurrentAlt { get { return service.CurrentAlt; } }
			public double FreqGPS { get { return service.FreqGPS; } }
			public ChuteLogServiceBinder(ChuteLogService _service)
			{
				service = _service;
			}
			public void Init()
			{
				service.Init();
			}
			public void InitBluetooth()
			{
				// TODO : faire ça dans un thread séparé
				service.InitializeBluetoothAltitude();
			}
			public void InitGPS()
			{
				// TODO : faire ça dans un thread séparé
				service.InitializeLocationManager();
			}
			public void OnServiceStatusChanged(object sender, ServiceStatusChangedType status, string message = "")
			{
				ServiceStatusChanged?.Invoke(sender, status, message);
			}
			public bool StartLogging()
			{
				return service.StartLogging(); // TODO : return false si pas init ou si KO init
			}
			public string EndLogging()
			{
				return service.EndLogging(); // TODO : return nom fichier CSV?
			}
		}

		public override IBinder OnBind(Intent intent)
		{
			Log.Verbose(TAG, "OnBind");
			binder = binder ?? new ChuteLogServiceBinder(this);
			return binder;
		}

		public override void OnCreate()
		{
			base.OnCreate();
			Log.Verbose(TAG, "OnCreate");

			//TODO : init GPS+BT et si OK notifier l'activité pour pouvoir dégriser le bouton de début d'enregistrement
		}

		public override void OnDestroy()
		{
			base.OnDestroy();
			Log.Verbose(TAG, "OnDestroy");
			EndLogging();
		}

		public override bool OnUnbind(Intent intent)
		{
			Log.Verbose(TAG, "OnUnbind");
			EndLogging();
			return base.OnUnbind(intent);
		}

		public override void OnRebind(Intent intent)
		{
			base.OnRebind(intent);
			Log.Verbose(TAG, "OnRebind");
			EndLogging();
		}
		#endregion

		#region INIT
		public void Init()
		{
			okbluetooth = okgps = initended = false;
			// TODO : faire ça dans un thread!!!
			if (!InitializeBluetoothAltitude())
			{
				okbluetooth = false;
				//ExitActivity();
				//return;
			}
			else
			{
				okbluetooth = true;
				ListenBluetoothAltitude();
			}
			InitializeLocationManager();
			OnServiceStatusChanged(this, ServiceStatusChangedType.INIT_ENDED);
			// TODO virer ça ?
			_locationManager?.RequestLocationUpdates(_locationProvider, 0, 0, this);
			initended = true;
		}

		private void ResetBT()
		{
			okbluetooth = false;
			if (inputStream != null)
			{
				try { inputStream.Close(); } catch (Exception) { }
				inputStream = null;
			}

			if (outputStream != null)
			{
				try { outputStream.Close(); } catch (Exception) { }
				outputStream = null;
			}

			if (btsocket != null)
			{
				try { btsocket.Close(); } catch (Exception) { }
				btsocket = null;
			}
		}

		public void InitializeLocationManager()
		{
			_locationManager = (LocationManager)GetSystemService(LocationService);
			Criteria criteriaForLocationService = new Criteria
			{
				Accuracy = Accuracy.Fine
			};
			IList<string> acceptableLocationProviders = _locationManager.GetProviders(criteriaForLocationService, true);

			if (acceptableLocationProviders.Any())
			{
				_locationProvider = acceptableLocationProviders.First();
			}
			else
			{
				_locationProvider = string.Empty;
			}
			Log.Debug(TAG, "Using " + _locationProvider + ".");
			okgps = true;
		}

		public bool InitializeBluetoothAltitude()
		{
			ResetBT();
			bluetoothAdapter = BluetoothAdapter.DefaultAdapter;
			var bondeddevices = bluetoothAdapter.BondedDevices;

			if (bondeddevices.Count <= 0)
			{
				Toast.MakeText(this, "Please Pair the Device first", ToastLength.Short).Show();
				return false;
			}
			else
			{
				foreach (BluetoothDevice devicetmp in bondeddevices)
				{
					if (devicetmp.Address.Equals(DEVICE_ADDRESS))
					{
						device = devicetmp;
						break;
					}
				}
				if (device == null)
					device = bondeddevices.FirstOrDefault(d => d.Name.Equals("HC-06", StringComparison.OrdinalIgnoreCase));
				if (device == null)
					device = bluetoothAdapter.GetRemoteDevice(DEVICE_ADDRESS);
				if (device == null)
				{
					Toast.MakeText(this, "Unable to find bluetooth altitude sensor!!!", ToastLength.Short).Show();
					return false;
				}
			}
			try
			{
				btsocket = device.CreateRfcommSocketToServiceRecord(PORT_UUID);
				btsocket.Connect();
			}
			catch (Exception ex)
			{

				OnServiceStatusChanged(this, ServiceStatusChangedType.ERROR, "Unable to connect to bluetooth altitude sensor (\"" + ex.Message + "\")!!!");
				return false;
			}
			inputStream = btsocket.InputStream;
			outputStream = btsocket.OutputStream;

			return true;
		}
		#endregion

		#region ROUTINES DE  POSITIONNEMENT
		void ListenBluetoothAltitude()
		{
			Handler handler = new Handler();
			byte[] inBuf = new byte[4];
			byte[] buf = new byte[2] { 65, 0x0A };
			int tmpLen = 0, readLen = 0;
			new Thread(() =>
			{
				if (okbluetooth)
				{
					//send Reset
					buf[0] = 82;//'R'
					buf[1] = 0x0A;
					outputStream.Write(buf, 0, 2);
				}
				while (okbluetooth && Thread.CurrentThread.IsAlive)
				{
					try
					{
						tmpLen = readLen = 0;
						//send GetAltitude
						buf[0] = 65;//'A'
						buf[1] = 0x0A;
						//LogDebug("BT Send Command...", false);
						outputStream.Write(buf, 0, 2);
						//LogDebug("sent");
						while (readLen < inBuf.Length && (tmpLen = inputStream.Read(buf, 0, 1)) > 0)
						{
							//LogDebug("BT Read "+(int)buf[0]);
							inBuf[readLen] = buf[0];
							readLen += 1;
						}
						if (readLen == 4)
							currentpos.alt = BitConverter.ToSingle(inBuf, 0);
						//if (readLen < inBuf.Length - 1)
						//	inBuf[readLen] = 0;
						//if (readLen > 0)
						//	LogDebug("BT Received " + readLen + "B");// : " + Encoding.ASCII.GetString(inBuf.Take(readLen).ToArray()));
					}
					catch (Exception ex)
					{
						okbluetooth = false;
						//handler.Post(() => { Toast.MakeText(this, ex.Message, ToastLength.Long); });
						OnServiceStatusChanged(this, ServiceStatusChangedType.ERROR, "BT Error : " + ex.Message);
					}
					Thread.Sleep(periode_bt_alt);
				}
			}).Start();
		}

		#region ILocationProvider
		public void OnLocationChanged(Location location)
		{
			Location _currentLocation = location;
			TimeSpan tsgps = DateTime.Now - datePrevLoc;
			datePrevLoc = DateTime.Now;
			if (_currentLocation == null)
				okgps = false;
			else
			{
				okgps = true;
				currentpos.lat = _currentLocation.Latitude;
				currentpos.lon = _currentLocation.Longitude;
				if (!okbluetooth)
					currentpos.alt = _currentLocation.Altitude;
				freq_gps = 1.0f / (tsgps.TotalMilliseconds / 1000.0f);
			}
			OnServiceStatusChanged(this, ServiceStatusChangedType.LOCATION_CHANGED);
		}

		public void OnProviderDisabled(string provider) { }

		public void OnProviderEnabled(string provider) { }

		public void OnStatusChanged(string provider, [GeneratedEnum] Availability status, Bundle extras) { }
		#endregion
		#endregion

		#region COMMUNICATION
		public void OnServiceStatusChanged(object sender, ServiceStatusChangedType status, string message = "")
		{
			binder?.OnServiceStatusChanged(sender, status, message);
		}
		#endregion

		/// <summary>
		/// Démarre l'enregistrement de chute
		/// </summary>
		public bool StartLogging()
		{
			//TODO
			if (recording)
				return false;
			_locationManager?.RequestLocationUpdates(_locationProvider, 0, 0, this);
			dtStartLogging = DateTime.Now;
			recording = true;
			new Thread(() =>
			{
				while (recording)
				{
					try
					{
						//if (Math.Abs(currentpos.lat) > double.Epsilon && Math.Abs(currentpos.lon) > double.Epsilon)
						currentpos.millis = (DateTime.Now - dtStartLogging).TotalMilliseconds;
						points.Add(currentpos);
						// tous les 50 points on sauve le CSV (au cas où le service se fait tuer)
						if (points.Count > 50)
							SaveCSV();
						Thread.Sleep(periode_freq_logging);
						if (currentpos.millis > AUTO_STOP_TIMER)
							EndLogging();
					}
					catch (Java.Lang.InterruptedException e)
					{
						Log.Error(TAG, e, "Error while logging chute");
					}
					Log.Verbose(TAG, "tick");
				}

			}).Start();
			return true;
		}

		/// <summary>
		/// Termine l'enregistrement de chute
		/// </summary>
		public string EndLogging()
		{
			recording = false;
			// TODO : deinit (fin scrute gps/bluetooth)
			_locationManager?.RemoveUpdates(this);
			if (points?.Any() ?? false)
				return SaveCSV();
			return null;
		}

		/// <summary>
		/// Sauvegarde le log de chute dans un csv
		/// </summary>
		private string SaveCSV()
		{
			string filename = "ChuteLog_" + dtStartLogging.ToString("yyyyMMddHHmmss") + ".csv";
			try
			{
				if (Android.OS.Environment.ExternalStorageState == Android.OS.Environment.MediaMounted)
				{
					var documentsPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
					documentsPath = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads).Path;
					filename = Path.Combine(documentsPath, filename);
					File.AppendAllLines(filename, points.Select(p =>
					string.Format("{0:f5};{1:f5};{2:f5};{3:f5}", p.millis, p.lat, p.lon, p.alt)
					));
				}
				else
				{
					//TODO : enregistrer à un autre endroit, pour l'instant je jette une exception pour signifier qu'il y'a eu un problème
					throw new Exception("SAUVEGARDE IMPOSSIBLE");
				}
				// si tout s'est bien passé on vide la liste
				points.Clear();
			}
			catch (Exception ex)
			{
				string strMessage = "Error while saving CSV \"" + filename + "\" : " + ex.ToString();
				Log.Error(TAG, strMessage);
				filename = null;
				OnServiceStatusChanged(this, ServiceStatusChangedType.ERROR, strMessage);
			}
			return filename;
		}
	}
}
