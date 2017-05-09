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

namespace ChuteLog
{
	[Activity(Label = "ChuteLog", MainLauncher = true, Icon = "@drawable/icon")]
	public class MainActivity : Activity, ILocationListener
	{
		private const string DEVICE_ADDRESS = "20:13:10:15:33:66";
		private UUID PORT_UUID = UUID.FromString("00001101-0000-1000-8000-00805f9b34fb");//Serial Port Service ID
		static readonly string TAG = "X:" + typeof(MainActivity).Name;
		BluetoothAdapter bluetoothAdapter;
		BluetoothDevice device;
		BluetoothSocket socket;
		Stream inputStream, outputStream;
		Location _currentLocation;
		LocationManager _locationManager;
		DateTime datePrevLoc = DateTime.Now;

		string _locationProvider;
		TextView tvGPSStatus;
		Button btnGo;

		readonly string[] PermissionsLocation =
		{
			Manifest.Permission.AccessCoarseLocation,
			Manifest.Permission.AccessFineLocation
		};

		const int RequestLocationId = 0;
		async Task TryGetLocationAsync()
		{
			if ((int)Build.VERSION.SdkInt < 23)
			{
				await GetLocationAsync();
				return;
			}

			await GetLocationPermissionAsync();
		}
		async Task GetLocationPermissionAsync()
		{
			//Check to see if any permission in our group is available, if one, then all are
			const string permission = Manifest.Permission.AccessFineLocation;
			if (CheckSelfPermission(permission) == (int)Permission.Granted)
			{
				await GetLocationAsync();
				return;
			}

			//need to request permission
			if (ShouldShowRequestPermissionRationale(permission))
			{
				//Explain to the user why we need to read the contacts
				new AlertDialog.Builder(this).SetTitle("Title")
					.SetMessage("Location access is required to show coffee shops nearby.")
					.SetIcon(Android.Resource.Drawable.IcDialogAlert)
					.SetPositiveButton(Android.Resource.String.Ok, (o, e) =>
					{
						RequestPermissions(PermissionsLocation, RequestLocationId);
					})
					//.SetNegativeButton(Android.Resource.String.No, (o, e) => { })
					.Show();
				return;
			}
			//Finally request permissions with the list of permissions and Id
			RequestPermissions(PermissionsLocation, RequestLocationId);
		}
		public override async void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
		{
			switch (requestCode)
			{
				case RequestLocationId:
					{
						if (grantResults[0] == Permission.Granted)
						{
							//Permission granted
							Toast.MakeText(this, "Location permission is available, getting lat/long.", ToastLength.Short).Show();

							await GetLocationAsync();
						}
						else
						{
							//Permission Denied :(
							//Disabling location functionality
							Toast.MakeText(this, "Location permission is denied.", ToastLength.Short).Show();
						}
					}
					break;
			}
		}
		async Task GetLocationAsync()
		{
			tvGPSStatus.Text = "Getting Location";
			try
			{
				var locator = CrossGeolocator.Current;
				locator.DesiredAccuracy = 100;
				var position = await locator.GetPositionAsync(20000);

				tvGPSStatus.Text = string.Format("Lat: {0}  Long: {1}", position.Latitude, position.Longitude);
			}
			catch (Exception ex)
			{
				tvGPSStatus.Text = "Unable to get location: " + ex.ToString();
			}
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
			
			//?bluetoothAdapter.GetRemoteDevice(DEVICE_ADDRESS)
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

		//https://developer.xamarin.com/recipes/android/os_device_resources/gps/get_current_device_location/
		//https://developer.xamarin.com/guides/android/getting_started/hello,android/hello,android_quickstart/
		protected override void OnCreate(Bundle bundle)
		{
			base.OnCreate(bundle);

			// Set our view from the "main" layout resource
			SetContentView (Resource.Layout.Main);
			btnGo = this.FindViewById<Button>(Resource.Id.btnGo);
			tvGPSStatus = FindViewById<TextView>(Resource.Id.tvGPSStatus);

			Task task = TryGetLocationAsync();
			task.Wait();
			SaveFile();
			Toast.MakeText(this, ReadFile(), ToastLength.Long).Show();
			if (!InitializeBluetoothAltitude())
			{
				ShowHome();
				return;
			}
			InitializeLocationManager();

			btnGo.Click += BtnGo_Click;
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

		private void ShowHome()
		{
			Intent intent = new Intent(Intent.ActionMain);
			intent.AddCategory(Intent.CategoryHome);
			intent.SetFlags(ActivityFlags.NewTask);
			StartActivity(intent);
		}

		private void BtnGo_Click(object sender, System.EventArgs e)
		{
			Toast.MakeText(this, "click", ToastLength.Long).Show();
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
			_currentLocation = location;
			TimeSpan tsgps = DateTime.Now - datePrevLoc;
			datePrevLoc = DateTime.Now;
			if (_currentLocation == null)
			{
				btnGo.Enabled = false;
				tvGPSStatus.Text = "GPS KO.";
			}
			else
			{
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

		public void OnProviderDisabled(string provider)
		{

		}

		public void OnProviderEnabled(string provider)
		{
		}

		public void OnStatusChanged(string provider, [GeneratedEnum] Availability status, Bundle extras)
		{
		}
	}
}

