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
	class ChuteLogServiceConnection : Java.Lang.Object, IServiceConnection
	{
		MainActivity activity;

		public ChuteLogServiceConnection(MainActivity activity)
		{
			this.activity = activity;
		}

		public void OnServiceConnected(ComponentName name, IBinder servicebinder)
		{
			var binder = servicebinder as ChuteLogService.ChuteLogServiceBinder;
			if (binder != null)
			{
				activity.LogDebug("OnServiceConnected");
				activity.binder = binder;
				activity.isBound = true;
				binder.ServiceStatusChanged -= activity.OnServiceStatusChanged;
				binder.ServiceStatusChanged += activity.OnServiceStatusChanged;
				activity.TryInit();
			}
		}

		public void OnServiceDisconnected(ComponentName name)
		{
			activity.isBound = false;
		}
	}

	[BroadcastReceiver(Enabled = true, Exported = false)]
	public class BluetoothStateReceiver : BroadcastReceiver
	{
		MainActivity activity;
		public BluetoothStateReceiver()
		{

		}
		public BluetoothStateReceiver(MainActivity _activity)
		{
			activity = _activity;
		}
		public override void OnReceive(Context context, Intent intent)
		{
			String action = intent.Action;

			if (action.Equals(BluetoothAdapter.ActionStateChanged))
			{
				int bluetoothState = intent.GetIntExtra(BluetoothAdapter.ExtraState,
																						BluetoothAdapter.Error);
				switch (bluetoothState)
				{
					case (int)State.On:
						activity?.TryInit(true);
						break;
				}
			}
		}
	}

	[Activity(Label = "ChuteLog", MainLauncher = true, Icon = "@drawable/icon")]
	public class MainActivity : Activity
	{
		static readonly string TAG = "X:" + typeof(MainActivity).Name;

		TextView tvGPSStatus, tvLog;
		Button btnGo, btnInitBT, btnInitGPS;
		Handler handler;

		public ChuteLogService.ChuteLogServiceBinder binder = null;
		public bool isBound = false;
		ChuteLogServiceConnection chutelogServiceConnection;
		BluetoothStateReceiver bluetoothstatereceiver;

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
		bool bPermLoc = false, bPermBluetooth = false, bInit = false;

		public bool IsRecording
		{
			get
			{
				return binder?.IsRecording ?? false;
			}
		}

		public void TryInit(bool force = false)
		{
			// si déjà initialisé alors on quitte
			if (!force && bInit)
				return;
			if ((int)Build.VERSION.SdkInt < 23)
			{
				bPermLoc = true;
				bPermBluetooth = true;
				Init();
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

		void Init()
		{
			if (!bPermLoc/* || !bPermBluetooth*/)
			{
				ExitActivity();
				return;
			}
			// test si le bluetooth doit être activé
			BluetoothAdapter bluetoothAdapter = BluetoothAdapter.DefaultAdapter;
			if (bluetoothAdapter == null)
			{
				Toast.MakeText(this, "Device doesnt Support Bluetooth", ToastLength.Short).Show();
				return;
			}
			if (!bluetoothAdapter.IsEnabled)
			{
				Intent enableAdapter = new Intent(BluetoothAdapter.ActionRequestEnable);
				StartActivityForResult(enableAdapter, 0);
				// le retour est géré grâce au BluetoothStateReceiver
				return;
			}

			LogDebug("Init binder==" + (binder?.GetHashCode().ToString() ?? "null"));
			binder?.Init();
			// todo ;faire la suite  au retour de l'init du service et pas avnat
			UpdateStatus();
			bInit = true;
			UpdateStatus();
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
			btnInitBT = this.FindViewById<Button>(Resource.Id.btnInitBT);
			btnInitGPS = this.FindViewById<Button>(Resource.Id.btnInitGPS);
			tvGPSStatus = FindViewById<TextView>(Resource.Id.tvGPSStatus);
			tvLog = FindViewById<TextView>(Resource.Id.tvLog);
			tvLog.MovementMethod = new Android.Text.Method.ScrollingMovementMethod();

			handler = new Handler();

			IntentFilter filter = new IntentFilter(BluetoothAdapter.ActionStateChanged);
			bluetoothstatereceiver = new BluetoothStateReceiver(this);
			RegisterReceiver(bluetoothstatereceiver, filter);

			LogDebug("Create!");

			btnGo.Click -= BtnGo_Click;
			btnGo.Click += BtnGo_Click;
			btnInitBT.Click -= BtnInitBT_Click;
			btnInitBT.Click += BtnInitBT_Click;
			btnInitGPS.Click -= btnInitGPS_Click;
			btnInitGPS.Click += btnInitGPS_Click;
			btnGo.Text = this.Resources.GetString(IsRecording ? Resource.String.btnGo_Stop : Resource.String.btnGo_Go);
		}

		protected override void OnStart()
		{
			base.OnStart();
			LogDebug("start! Android Build SDK Ver "+ Build.VERSION.SdkInt.ToString());
			var chutelogServiceIntent = new Intent(this, typeof(ChuteLogService));// ("net.pasutto.ChuteLogService");
			chutelogServiceConnection = new ChuteLogServiceConnection(this);
			bool b = BindService(chutelogServiceIntent, chutelogServiceConnection, Bind.AutoCreate);
			//StartService(chutelogServiceIntent);
			LogDebug("BindService returned " + b.ToString());
		}

		protected override void OnDestroy()
		{
			base.OnDestroy();
			LogDebug("destroy!");
			UnregisterReceiver(bluetoothstatereceiver);
			// on ne détruit pas le service si on quitte l'application!!!
			/*if (isBound)
			{
				UnbindService(chutelogServiceConnection);
				isBound = false;
			}*/
		}

		private void BtnInitBT_Click(object sender, EventArgs e)
		{
			btnInitBT.Enabled = false;
			binder?.InitBluetooth();
			// TODO : faire la suite dans le onstatuschanged
			btnInitBT.Enabled = true;
			UpdateStatus();
		}

		private void btnInitGPS_Click(object sender, EventArgs e)
		{
			btnInitGPS.Enabled = false;
			binder?.InitGPS();
			// TODO : faire la suite dans le onstatuschanged
			btnInitGPS.Enabled = true;
			UpdateStatus();
		}

		public void LogDebug(string text, bool bcr = true)
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
		
		public void OnServiceStatusChanged(object sender, ChuteLogService.ServiceStatusChangedType status, string message = "")
		{
			switch (status)
			{
				case ChuteLogService.ServiceStatusChangedType.INIT_ENDED:
					btnGo.Enabled = /*(binder?.IsBluetoothOK ?? false) && */(binder?.IsGPSOK ?? false);
					break;
				case ChuteLogService.ServiceStatusChangedType.ERROR:
					Toast.MakeText(this, message, ToastLength.Short).Show();
					break;
				case ChuteLogService.ServiceStatusChangedType.LOCATION_CHANGED:
					UpdateStatus();
					LogDebug("LocationChanged");
					break;
			}
		}

		void UpdateStatus()
		{
			tvGPSStatus.Text =
				string.Format("{3} : GPS {0} ({1:f1} Hz), Bluetooth {2}, Alt={4} m",
				(binder?.IsGPSOK ?? false) ? "OK" : "KO", (binder?.FreqGPS ?? 0.0d), (binder?.IsBluetoothOK ?? false) ? "OK" : "KO",
				IsRecording ? "Recording" : "Idle", (int)Math.Round(binder?.CurrentAlt ?? 0.0d));
		}

		private void BtnGo_Click(object sender, System.EventArgs e)
		{
			if (binder?.IsRecording ?? false)
			{
				btnGo.Text = this.Resources.GetString(Resource.String.btnGo_Go);
				string strFile = binder?.EndLogging();
				if (strFile != null)
					Toast.MakeText(this, "File saved in \"" + strFile + "\"", ToastLength.Short).Show();
			}
			else
			{
				if ((binder?.StartLogging() ?? false) && (binder?.IsRecording ?? false))
					btnGo.Text = this.Resources.GetString(Resource.String.btnGo_Stop);
			}
			return;
		}

		protected override void OnResume()
		{
			base.OnResume();
			LogDebug("Resume!");
		}

		protected override void OnPause()
		{
			base.OnPause();
			LogDebug("Pause!");
		}
	}
}

