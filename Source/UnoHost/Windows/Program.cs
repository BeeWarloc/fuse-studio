﻿using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Windows.Forms;
using Outracks.Fusion;
using Outracks.Fusion.Windows;
using Outracks.UnoHost.Windows.Protocol;

namespace Outracks.UnoHost.Windows
{
	using Extensions;
	using Fuse.Analytics;
	using IO;

	class Program
	{
		static Program()
		{
			Thread.CurrentThread.SetInvariantCulture();
		}

		[STAThread]
		static void Main(string[] argsArray)
		{
			var shell = new Shell();
			var systemId = SystemGuidLoader.LoadOrCreateOrEmpty();
			var sessionId = Guid.NewGuid();
			var log = ReportFactory.GetReporter(systemId, sessionId, "UnoHost");
			AppDomain.CurrentDomain.ReportUnhandledExceptions(log);

			DpiAwareness.SetDpiAware(DpiAwareness.ProcessDpiAwareness.SystemAware);

			NativeResources.Load();

			var args = UnoHostArgs.RemoveFrom(argsArray.ToList(), shell);

			// Load metadata
			var unoHostProject = UnoHostProject.Load(args.MetadataPath, shell);

			var form = new DpiAwareForm()
			{
				FormBorderStyle = FormBorderStyle.None,
				ShowInTaskbar = false
			};

			//if (args.IsDebug)
			//	InitDebugMode(form);

			var unoControl = new UnoControl(form, log);
			form.Controls.Add(unoControl);
			form.ShowIcon = false;


			var openGlVersion = new Subject<OpenGlVersion>();
			var backgroundQueue = new QueuedDispatcher();
			var lostFocus = Observable.FromEventPattern(unoControl, "LostFocus").ObserveOn(backgroundQueue);

			var messagesTo = new Subject<IBinaryMessage>();

			var output = Observable.Merge(
				messagesTo.Do(message => Console.WriteLine(message.Type)),
				openGlVersion.Select(OpenGlVersionMessage.Compose),
				Observable.FromEventPattern(unoControl, "GotFocus").Select(_ => WindowFocusMessage.Compose(FocusState.Focused)),
				lostFocus.Select(_ => WindowFocusMessage.Compose(FocusState.Blurred)),
				lostFocus.Select(_ => WindowContextMenuMessage.Compose(false)),
				Observable.FromEventPattern<System.Windows.Forms.MouseEventArgs>(unoControl, "MouseUp")
					.Where(m => m.EventArgs.Button == System.Windows.Forms.MouseButtons.Right)
					.Select(_ => WindowContextMenuMessage.Compose(true)),
				Observable.FromEventPattern<System.Windows.Forms.MouseEventArgs>(unoControl, "MouseDown")
					.Select(_ => WindowContextMenuMessage.Compose(false)),
				Observable.FromEventPattern<System.Windows.Forms.MouseEventArgs>(unoControl, "MouseWheel")
					.Select(m => WindowMouseScrollMessage.Compose(m.EventArgs.Delta)),
				Observable.FromEventPattern<KeyEventArgs>(unoControl, "KeyDown")
					.Select(m => WindowKeyDown.Compose(m.EventArgs.KeyCode)),
				Observable.FromEventPattern<KeyEventArgs>(unoControl, "KeyUp")
					.Select(m => WindowKeyUp.Compose(m.EventArgs.KeyCode)));

			args.OutputPipe.BeginWritingMessages(
				"Designer", 
				ex => Console.WriteLine("UnoHost failed to write message to designer: " + ex),
				output.ObserveOn(new QueuedDispatcher()));

			unoControl.Initialize(unoHostProject, openGlVersion);

			// Set hand cursor so we know when we're interacting with the UnoHost and not in the designer
			unoControl.Cursor = System.Windows.Forms.Cursors.Hand;

			// notify studio about the window
			messagesTo.OnNext(WindowCreatedMessage.Compose(form.Handle));

			var dispatcher = new PollingDispatcher(Thread.CurrentThread);
			
			unoControl.PerFrame.Subscribe(f => dispatcher.DispatchCurrent());


			var density = Observable.Return(new Ratio<Points, Pixels>(1));

			var size = Observable.FromEventPattern(unoControl, "SizeChanged")
				.StartWith(new EventPattern<object>(null, null))
				.Select(_ => unoControl.Size.ToSize())
				.CombineLatest(density, (s, d) => s.Mul(d))
				.Transpose();



			var messagesFrom = args.InputPipe
				.ReadMessages("Designer")
				.RefCount()
				.ObserveOn(dispatcher)
				.Publish();
			messagesFrom
				.SelectSome(MouseEventMessage.TryParse)
				.Subscribe(unoControl.OnMouseEvent);

			messagesFrom.Subscribe(next => { }, e => form.Exit(1), () => form.Exit(0));

			messagesFrom
				.SelectSome(SetSurfaceMessage.TryParse)
				.Subscribe(texture => unoControl.SetBackingSurface(texture));

			// Run the uno entrypoints, this initializes Uno.Application.Current

			unoHostProject.ExecuteStartupCode();
			var app = Uno.Application.Current as dynamic;


			// Init plugins

			FusionImplementation.Initialize(dispatcher, args.UserDataPath, app.Reflection);
			var overlay = PluginManager.Initialize(messagesFrom, messagesTo, dispatcher, unoControl.PerFrame, size);
			app.ResetEverything(true, overlay);

	
			// Ready to go

			messagesFrom.Connect();
			messagesTo.OnNext(new Ready());
			
			unoControl.Run();
		}

		
		public static void InitDebugMode(DpiAwareForm form)
		{
			form.FormBorderStyle = FormBorderStyle.Sizable;
			form.Size = new System.Drawing.Size(1000, 1000);
			form.StartPosition = FormStartPosition.CenterScreen;
			form.FpsProfiler
				.GetAverageFramesPerSecond(40)
				.Subscribe(fps => form.Text = fps.ToString());
			form.Show();
		}
	}
}
