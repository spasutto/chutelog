using Android.App;
using Android.Widget;
using Android.OS;
using Android.Locations;
using System.Collections.Generic;
using Android.Util;
using System.Linq;
using Android.Runtime;
using System;
using Android.Bluetooth;
using Android.Content;
using Java.Util;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Android;
using Android.Content.PM;
using System.Threading;

namespace ChuteLog
{
	[Activity(Label = "ChuteLog", MainLauncher = true, Icon = "@drawable/icon")]
	public class MainActivity : Activity, ILocationListener
	{
		struct LogPoint
		{
			public double lat, lon, alt, millis;
		}

		private const int FREQ_BT_ALT = 3;
		private const int FREQ_LOGGING = 3;
		//private const string DEVICE_ADDRESS = "20:13:10:15:33:66";//HC-05
		private const string DEVICE_ADDRESS = "98:d3:31:90:2d:0e";//HC-06
		private UUID PORT_UUID = UUID.FromString("00001101-0000-1000-8000-00805f9b34fb");//Serial Port Service ID
		static readonly string TAG = "X:" + typeof(MainActivity).Name;
		BluetoothAdapter bluetoothAdapter;
		BluetoothDevice device;
		BluetoothSocket btsocket;
		Stream inputStream, outputStream;
		LocationManager _locationManager;
		LogPoint currentpos;
		double freq_gps = 0.0f;
		DateTime datePrevLoc = DateTime.Now, dtStartLogging;

		string _locationProvider;
		TextView tvGPSStatus, tvLog;
		Button btnGo, btnInitBT, btnInitGPS;
		Handler handler;
		
		List<LogPoint> points = new List<LogPoint>();

		readonly string[] PermissionsLocation =
		{
			Manifest.Permission.AccessCoarseLocation,
			Manifest.Permission.AccessFineLocation
		};
		readonly string[] PermissionsBluetooth =
		{
			Manifest.Permission.Bluetooth,
			Manifest.Permission.BluetoothAdmin
		};
		const int RequestLocationId = 0, RequestBluetoothId = 1;
		bool bPermLoc = false, bPermBluetooth = false, bOkBluetooth = false, bOkGPS = false, bRecording = false, bInit = false;

		public bool CanRecord { get { return bOkBluetooth && bOkGPS; } }

		void TryInit()
		{
			handler = new Handler();
			if ((int)Build.VERSION.SdkInt < 23)
			{
				bPermLoc = true;
				bPermBluetooth = true;
				return;
			}

			GetPermissions();
		}
		void GetPermissions()
		{
			//Check to see if any permission in our group is available, if one, then all are
			const string permissionloc = Manifest.Permission.AccessFineLocation;
			const string permissionbt = Manifest.Permission.BluetoothAdmin;
			if (CheckSelfPermission(permissionloc) == (int)Permission.Granted)
				bPermLoc = true;
			if (CheckSelfPermission(permissionbt) == (int)Permission.Granted)
				bPermBluetooth = true;
			if (/*bPermBluetooth && */bPermLoc)
			{
				Init();
				return;
			}

			Action requestpermissions = () =>
			{
				RequestPermissions(PermissionsLocation, RequestLocationId);
				RequestPermissions(PermissionsBluetooth, RequestBluetoothId);
			};

			//need to request permission
			if (ShouldShowRequestPermissionRationale(permissionloc)
				|| ShouldShowRequestPermissionRationale(permissionbt))
			{
				//Explain to the user why we need to read the contacts
				new AlertDialog.Builder(this).SetTitle("Title")
					.SetMessage("You need to give the permission to use bluetooth and GPS.")
					.SetIcon(Android.Resource.Drawable.IcDialogAlert)
					.SetPositiveButton(Android.Resource.String.Ok, (o, e) => requestpermissions())
					//.SetNegativeButton(Android.Resource.String.No, (o, e) => { })
					.Show();
				return;
			}
			else
				//Finally request permissions with the list of permissions and Id
				requestpermissions();
		}
		int reqnbr = 2;
		public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
		{
			switch (requestCode)
			{
				case RequestLocationId:
					{
						if (grantResults[0] != Permission.Granted)
						{
							//Permission Denied :(
							//Disabling location functionality
							Toast.MakeText(this, "Location permission is denied.", ToastLength.Short).Show();
							bPermLoc = false;
						}
						else
							bPermLoc = true;
					}
					break;
				case RequestBluetoothId:
					{
						if (grantResults[0] != Permission.Granted)
						{
							//Permission Denied :(
							//Disabling location functionality
							Toast.MakeText(this, "Bluetooth permission is denied.", ToastLength.Short).Show();
							bPermBluetooth = false;
						}
						else
							bPermBluetooth = true;
					}
					break;
			}
			if (--reqnbr == 0)
				Init();
		}

		void InitializeLocationManager()
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
		}

		bool InitializeBluetoothAltitude()
		{
			bluetoothAdapter = BluetoothAdapter.DefaultAdapter;

			if (bluetoothAdapter == null)
			{
				Toast.MakeText(this, "Device doesnt Support Bluetooth", ToastLength.Short).Show();
				return false;
			}
			if (!bluetoothAdapter.IsEnabled)
			{
				Intent enableAdapter = new Intent(BluetoothAdapter.ActionRequestEnable);
				StartActivityForResult(enableAdapter, 0);
				return false;
			}

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
				Toast.MakeText(this, "Unable to connect to bluetooth altitude sensor!!!", ToastLength.Short).Show();
				LogDebug(ex.Message);
				return false;
			}
			inputStream = btsocket.InputStream;
			outputStream = btsocket.OutputStream;

			return true;
		}

		private void ResetBT()
		{
			bOkBluetooth = false;
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

		//https://blog.xamarin.com/requesting-runtime-permissions-in-android-marshmallow/
		//https://developer.xamarin.com/recipes/android/os_device_resources/gps/get_current_device_location/
		//https://developer.xamarin.com/guides/android/getting_started/hello,android/hello,android_quickstart/
		//https://www.allaboutcircuits.com/projects/control-an-arduino-using-your-phone/
		protected override void OnCreate(Bundle bundle)
		{
			base.OnCreate(bundle);
			LogDebug("Create!");

			// Set our view from the "main" layout resource
			SetContentView(Resource.Layout.Main);
			btnGo = this.FindViewById<Button>(Resource.Id.btnGo);
			btnInitBT = this.FindViewById<Button>(Resource.Id.btnInitBT);
			btnInitGPS = this.FindViewById<Button>(Resource.Id.btnInitGPS);
			tvGPSStatus = FindViewById<TextView>(Resource.Id.tvGPSStatus);
			tvLog = FindViewById<TextView>(Resource.Id.tvLog);
			tvLog.MovementMethod = new Android.Text.Method.ScrollingMovementMethod();

			btnGo.Click -= BtnGo_Click;
			btnGo.Click += BtnGo_Click;
			btnInitBT.Click -= BtnInitBT_Click;
			btnInitBT.Click += BtnInitBT_Click;
			btnInitGPS.Click -= btnInitGPS_Click;
			btnInitGPS.Click += btnInitGPS_Click;
			btnGo.Text = this.Resources.GetString(bRecording ? Resource.String.btnGo_Stop : Resource.String.btnGo_Go);
			if (!bInit)
				TryInit();
		}

		private void BtnInitBT_Click(object sender, EventArgs e)
		{
			btnInitBT.Enabled = false;
			ResetBT();
			if (!InitializeBluetoothAltitude())
			{
				bOkBluetooth = false;
				//ExitActivity();
				//return;
			}
			else
			{
				bOkBluetooth = true;
				ListenBluetoothAltitude();
			}
			btnInitBT.Enabled = true;
			UpdateStatus();
		}

		private void btnInitGPS_Click(object sender, EventArgs e)
		{
			btnInitGPS.Enabled = false;
			InitializeLocationManager();
			btnInitGPS.Enabled = true;
			UpdateStatus();
		}

		void LogDebug(string text, bool bcr = true)
		{
			handler?.Post(() =>
			{
				if (tvLog != null)
				{
					tvLog.Text += text + (bcr ? "\r\n" : string.Empty);
					int line = tvLog.LineCount - tvLog.MaxLines;
					int y = tvLog.Layout?.GetLineTop(line < 0 ? 0 : line) ?? -1;
					tvLog.ScrollTo(0, y);
				}
			});
		}

		void Init()
		{
			if (!bPermLoc/* || !bPermBluetooth*/)
			{
				ExitActivity();
				return;
			}
			if (!InitializeBluetoothAltitude())
			{
				bOkBluetooth = false;
				//ExitActivity();
				//return;
			}
			else
			{
				bOkBluetooth = true;
				ListenBluetoothAltitude();
			}
			UpdateStatus();
			InitializeLocationManager();
			bInit = true;
			UpdateStatus();
		}

		void LogData()
		{
			//Handler handler = new Handler();
			new Thread(() =>
			{
				while (bRecording)
				{
					points.Add(new LogPoint()
					{
						lat = currentpos.lat,
						lon = currentpos.lon,
						alt = currentpos.alt,
						millis = (DateTime.Now - dtStartLogging).TotalMilliseconds
					});
					Thread.Sleep((int)Math.Round(1000.0f / ((double)FREQ_LOGGING)));
				}
			}).Start();
		}

		void ListenBluetoothAltitude()
		{
			Handler handler = new Handler();
			byte[] inBuf = new byte[4];
			byte[] buf = new byte[2] { 65, 0x0A };
			int tmpLen = 0, readLen = 0;
			new Thread(() =>
			{
				if (bOkBluetooth)
				{
					//send Reset
					buf[0] = 82;//'R'
					buf[1] = 0x0A;
				}
				while (bOkBluetooth && Thread.CurrentThread.IsAlive)
				{
					try
					{
						tmpLen = readLen = 0;
						outputStream.Write(buf, 0, 2);
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
						//handler.Post(() => { Toast.MakeText(this, ex.Message, ToastLength.Long); });
						LogDebug("BT Error : " + ex.Message);
						bOkBluetooth = false;
					}
					Thread.Sleep((int)Math.Round(1000.0f / ((double)FREQ_BT_ALT)));
				}
			}).Start();
		}

		void SaveLog()
		{
			String filename = "ChuteLog_" + dtStartLogging.ToString("yyyyMMddHHmmss") + ".csv";
			if (Android.OS.Environment.ExternalStorageState == Android.OS.Environment.MediaMounted)
			{
				/*using (Stream fos = OpenFileOutput(FILENAME, FileCreationMode.Private))
				{
				}*/
				var documentsPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
				documentsPath = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads).Path;
				var filePath = Path.Combine(documentsPath, filename);
				LogDebug("Writing " +
					points.Count + " to " + filePath);
				File.WriteAllLines(filePath, points.Select(p =>
				string.Format("{0:f5};{1:f5};{2:f5};{3:f5}", p.millis, p.lat, p.lon, p.alt)
				));
				LogDebug("Done.");
				points.Clear();
			}
		}

		private void ExitActivity(int delay = 2000)
		{
			LogDebug("exit!");
			new Thread(() =>
			{
				Thread.Sleep(delay);
				this.Finish();
			}).Start();
			return;
			/*Intent intent = new Intent(Intent.ActionMain);
			intent.AddCategory(Intent.CategoryHome);
			intent.SetFlags(ActivityFlags.NewTask);
			StartActivity(intent);*/
		}

		void UpdateStatus()
		{
			tvGPSStatus.Text =
				string.Format("{3} : GPS {0} ({1:f1} Hz), Bluetooth {2}, Alt={4} m",
				bOkGPS ? "OK" : "KO", freq_gps, bOkBluetooth ? "OK" : "KO",
				bRecording ? "Recording" : "Idle", (int)Math.Round(currentpos.alt));
		}

		private void BtnGo_Click(object sender, System.EventArgs e)
		{
			//Toast.MakeText(this, "click", ToastLength.Long).Show();
			if (!bRecording && points.Any())
				Toast.MakeText(this, "Busy!!!", ToastLength.Long).Show();
			else if (!bRecording )
			{
				btnGo.Text = this.Resources.GetString(Resource.String.btnGo_Stop);
				bRecording = true;
				dtStartLogging = DateTime.Now;
				LogData();
			}
			else
			{
				btnGo.Text = this.Resources.GetString(Resource.String.btnGo_Go);
				bRecording = false;
				SaveLog();
			}
		}

		protected override void OnResume()
		{
			base.OnResume();
			LogDebug("Resume!");
			_locationManager?.RequestLocationUpdates(_locationProvider, 0, 0, this);
		}

		protected override void OnPause()
		{
			base.OnPause();
			LogDebug("Pause!");
			_locationManager?.RemoveUpdates(this);
		}

		public void OnLocationChanged(Location location)
		{
			LogDebug("LocationChanged");
			Location _currentLocation = location;
			TimeSpan tsgps = DateTime.Now - datePrevLoc;
			datePrevLoc = DateTime.Now;
			if (_currentLocation == null)
			{
				btnGo.Enabled = false;
				bOkGPS = false;
			}
			else
			{
				bOkGPS = true;
				currentpos.lat = _currentLocation.Latitude;
				currentpos.lon = _currentLocation.Longitude;
				if (!bOkBluetooth)
					currentpos.alt = _currentLocation.Altitude;
				if (!btnGo.Enabled)
					btnGo.Enabled = true;
				freq_gps = 1.0f / (tsgps.TotalMilliseconds / 1000.0f);
			}
			UpdateStatus();
		}

		public void OnProviderDisabled(string provider) { }

		public void OnProviderEnabled(string provider) { }

		public void OnStatusChanged(string provider, [GeneratedEnum] Availability status, Bundle extras) { }
	}
}

