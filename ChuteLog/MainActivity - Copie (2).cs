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
using Plugin.Geolocator;
using System.Threading;

namespace ChuteLog
{
	[Activity(Label = "ChuteLog", MainLauncher = true, Icon = "@drawable/icon")]
	public class MainActivity : Activity, ILocationListener
	{
		private const int FREQ_BT_ALT = 3;
		private const int FREQ_LOGGING = 3;
		private const string DEVICE_ADDRESS = "20:13:10:15:33:66";
		private UUID PORT_UUID = UUID.FromString("00001101-0000-1000-8000-00805f9b34fb");//Serial Port Service ID
		static readonly string TAG = "X:" + typeof(MainActivity).Name;
		BluetoothAdapter bluetoothAdapter;
		BluetoothDevice device;
		BluetoothSocket socket;
		Stream inputStream, outputStream;
		LocationManager _locationManager;
		double currentLat, currentLon, currentAlt;
		DateTime datePrevLoc = DateTime.Now;

		string _locationProvider;
		TextView tvGPSStatus;
		Button btnGo;
		
		struct LogPoint
		{
			public double lat, lon, alt;
		}

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
		bool bPermLoc = false, bPermBluetooth = false, bOkBluetooth = false, bRecording = false;

		void TryGetPermissions()
		{
			if ((int)Build.VERSION.SdkInt < 23)
			{
				bPermLoc = true;
				bPermBluetooth = true;
				return;
			}

			GetLocationPermission();
		}
		void GetLocationPermission()
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
					device = bluetoothAdapter.GetRemoteDevice(DEVICE_ADDRESS);
				if (device == null)
				{
					Toast.MakeText(this, "Unable to find bluetooth altitude sensor!!!", ToastLength.Short).Show();
					return false;
				}
			}
			try
			{
				socket = device.CreateRfcommSocketToServiceRecord(PORT_UUID);
				socket.Connect();
			}
			catch// (Exception ex)
			{
				Toast.MakeText(this, "Unable to connect to bluetooth altitude sensor!!!", ToastLength.Short).Show();
				return false;
			}
			inputStream = socket.InputStream;
			outputStream = socket.OutputStream;

			return true;
		}

		//https://blog.xamarin.com/requesting-runtime-permissions-in-android-marshmallow/
		//https://developer.xamarin.com/recipes/android/os_device_resources/gps/get_current_device_location/
		//https://developer.xamarin.com/guides/android/getting_started/hello,android/hello,android_quickstart/
		//https://www.allaboutcircuits.com/projects/control-an-arduino-using-your-phone/
		protected override void OnCreate(Bundle bundle)
		{
			base.OnCreate(bundle);

			// Set our view from the "main" layout resource
			SetContentView(Resource.Layout.Main);
			btnGo = this.FindViewById<Button>(Resource.Id.btnGo);
			tvGPSStatus = FindViewById<TextView>(Resource.Id.tvGPSStatus);

			TryGetPermissions();

			btnGo.Click += BtnGo_Click;
		}

		void Init()
		{
			if (!bPermLoc/* || !bPermBluetooth*/)
			{
				this.Finish();
				ExitActivity();
				return;
			}
			SaveFile();
			string textread = ReadFile();
			Toast.MakeText(this, textread, ToastLength.Long).Show();
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
			InitializeLocationManager();
		}

		void LogData()
		{
			//Handler handler = new Handler();
			new Thread(() =>
			{
				while (bRecording)
				{
					points.Add(new LogPoint() { lat = currentLat, lon = currentLon, alt = currentAlt });
					Thread.Sleep((int)Math.Round(1000.0f / ((double)FREQ_LOGGING)));
				}
			}).Start();
		}

		void ListenBluetoothAltitude()
		{
			Handler handler = new Handler();
			new Thread(() =>
			{
				byte[] inBuf = new byte[16];
				while (bOkBluetooth && Thread.CurrentThread.IsAlive)
				{
					try
					{
						byte[] buf = new byte[2] { 65, 0x0A };
						int tmpLen = 0;
						int readLen = 0;
						outputStream.Write(buf, 0, 2);
						while (readLen < inBuf.Length && (tmpLen = inputStream.Read(buf, 0, 1)) > 0)
						{
							inBuf[readLen] = buf[0];
							readLen += 1;
						}
					}
					catch (Exception ex)
					{
						handler.Post(() => { Toast.MakeText(this, ex.Message, ToastLength.Long); });
						bOkBluetooth = false;
					}
					Thread.Sleep((int)Math.Round(1000.0f / ((double)FREQ_BT_ALT)));
				}
			}).Start();
		}

		void SaveFile()
		{
			String FILENAME = "hello_file";
			byte[] thedata = Encoding.ASCII.GetBytes("hello world!");

			Stream fos = OpenFileOutput(FILENAME, FileCreationMode.Private);
			fos.Write(thedata, 0, thedata.Length);
			fos.Close();
		}

		string ReadFile()
		{
			String FILENAME = "hello_file";
			byte[] thedata;

			Stream fos = OpenFileInput(FILENAME);

			using (var stream = new MemoryStream())
			{
				byte[] buffer = new byte[2048]; // read in chunks of 2KB
				int bytesRead;
				while ((bytesRead = fos.Read(buffer, 0, buffer.Length)) > 0)
				{
					stream.Write(buffer, 0, bytesRead);
				}
				thedata = stream.ToArray();
			}

			return Encoding.ASCII.GetString(thedata);
		}

		private void ExitActivity(int delay = 2000)
		{
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

		private void BtnGo_Click(object sender, System.EventArgs e)
		{
			Toast.MakeText(this, "click", ToastLength.Long).Show();
			if (!bRecording)
			{
				btnGo.Text = this.Resources.GetString(Resource.String.btnGo_Stop);
				bRecording = true;
				LogData();
			}
			else
			{
				btnGo.Text = this.Resources.GetString(Resource.String.btnGo_Go);
				bRecording = false;
			}
		}

		protected override void OnResume()
		{
			base.OnResume();
			_locationManager?.RequestLocationUpdates(_locationProvider, 0, 0, this);
		}

		protected override void OnPause()
		{
			base.OnPause();
			_locationManager?.RemoveUpdates(this);
		}

		public void OnLocationChanged(Location location)
		{
			Location _currentLocation = location;
			TimeSpan tsgps = DateTime.Now - datePrevLoc;
			datePrevLoc = DateTime.Now;
			if (_currentLocation == null)
			{
				btnGo.Enabled = false;
				tvGPSStatus.Text = "GPS KO.";
			}
			else
			{
				currentLat = _currentLocation.Latitude;
				currentLon = _currentLocation.Longitude;
				if (!bOkBluetooth)
					currentAlt = _currentLocation.Altitude;
				if (!btnGo.Enabled)
					btnGo.Enabled = true;
				tvGPSStatus.Text = string.Format("GPS OK ({0:f1} Hz)",
					1.0f / (tsgps.TotalMilliseconds / 1000.0f));
			}
			/*tvLocation.Text = string.Format("{0:f6},{1:f6} ({2:f3} Hz)",
				_currentLocation.Latitude,
				_currentLocation.Longitude,
				1.0f / (tsgps.TotalMilliseconds / 1000.0f)
				);*/
		}

		public void OnProviderDisabled(string provider) { }

		public void OnProviderEnabled(string provider) { }

		public void OnStatusChanged(string provider, [GeneratedEnum] Availability status, Bundle extras) { }
	}
}

