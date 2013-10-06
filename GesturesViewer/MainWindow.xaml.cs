﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.IO;
using System.Windows.Input;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Media;

using Microsoft.Kinect;
using Microsoft.Win32;

using Kinect.Toolbox;
using Kinect.Toolbox.Record;
using Kinect.Toolbox.Voice;

using Common.Logging;

using HandInput.Engine;
using HandInput.Util;
using System.Runtime.Serialization.Formatters.Binary;

namespace HandInput.GesturesViewer {
  /// <summary>
  /// Interaction logic for MainWindow.xaml
  /// </summary>
  public partial class MainWindow {
    static readonly ILog Log = LogManager.GetCurrentClassLogger();
    static readonly ColorImageFormat ColorImageFormat = ColorImageFormat.RgbResolution640x480Fps30;
    static readonly DepthImageFormat DepthImageFormat = DepthImageFormat.Resolution640x480Fps30;
    static readonly int DepthWidth = 640, DepthHeight = 480;

    readonly ColorStreamManager colorManager = new ColorStreamManager();
    readonly DepthDisplayManager depthDisplayManager = new DepthDisplayManager(DepthWidth, DepthHeight);
    readonly TrainingManager trainingManager = new TrainingManager();
    readonly ContextTracker contextTracker = new ContextTracker();

    KinectSensor kinectSensor;

    AudioStreamManager audioManager;
    SkeletonDisplayManager skeletonDisplayManager;
    EyeTracker eyeTracker;
    bool displayDepth = false;

    KinectRecorder recorder;
    KinectAllFramesReplay replay;

    BindableNUICamera nuiCamera;

    int depthFrameNumber;
    BlockingCollection<KinectDataPacket> buffer = new BlockingCollection<KinectDataPacket>();
    CancellationTokenSource cancellationTokenSource;
    SaliencyDetector handTracker;
    SalientFeatureProcessor featureProcessor;
    FPSCounter fpsCounter = new FPSCounter();

    public MainWindow() {
      InitializeComponent();
    }

    void Kinects_StatusChanged(object sender, StatusChangedEventArgs e) {
      switch (e.Status) {
        case KinectStatus.Connected:
          if (kinectSensor == null) {
            kinectSensor = e.Sensor;
            Initialize();
          }
          break;
        case KinectStatus.Disconnected:
          if (kinectSensor == e.Sensor) {
            Clean();
            MessageBox.Show("Kinect was disconnected");
          }
          break;
        case KinectStatus.NotReady:
          break;
        case KinectStatus.NotPowered:
          if (kinectSensor == e.Sensor) {
            Clean();
            MessageBox.Show("Kinect is no more powered");
          }
          break;
        default:
          MessageBox.Show("Unhandled Status: " + e.Status);
          break;
      }
    }

    void Window_Loaded(object sender, RoutedEventArgs e) {
      this.Activate();
      try {
        //listen to any status change for Kinects
        KinectSensor.KinectSensors.StatusChanged += Kinects_StatusChanged;

        //loop through all the Kinects attached to this PC, and start the first that is connected without an error.
        foreach (KinectSensor kinect in KinectSensor.KinectSensors) {
          if (kinect.Status == KinectStatus.Connected) {
            kinectSensor = kinect;
            break;
          }
        }

        if (KinectSensor.KinectSensors.Count == 0)
          MessageBox.Show("No Kinect found");
        else
          Initialize();

      } catch (Exception ex) {
        MessageBox.Show(ex.Message);
      }
    }

    void Initialize() {
      if (kinectSensor == null)
        return;

      audioManager = new AudioStreamManager(kinectSensor.AudioSource);
      audioBeamAngle.DataContext = audioManager;

      kinectSensor.ColorStream.Enable(ColorImageFormat);

      kinectSensor.DepthStream.Enable(DepthImageFormat);

      kinectSensor.SkeletonStream.Enable(new TransformSmoothParameters {
        Smoothing = 0.5f,
        Correction = 0.5f,
        Prediction = 0.5f,
        JitterRadius = 0.05f,
        MaxDeviationRadius = 0.04f
      });

      kinectSensor.AllFramesReady += kinectRuntime_AllFrameReady;
      skeletonDisplayManager = new SkeletonDisplayManager(kinectSensor, kinectCanvas);
      kinectSensor.Start();

      nuiCamera = new BindableNUICamera(kinectSensor);

      elevationSlider.DataContext = nuiCamera;

      kinectDisplay.DataContext = colorManager;
    }

    void StartTracking() {
      cancellationTokenSource = new CancellationTokenSource();
      StopReply();
      var token = cancellationTokenSource.Token;
      Task.Factory.StartNew(() => HandTrackingTask(token), token);
    }

    void HandTrackingTask(CancellationToken token) {
      handTracker = new SaliencyDetector(DepthWidth, DepthHeight, kinectSensor.CoordinateMapper);
      while (kinectSensor != null && kinectSensor.IsRunning && !token.IsCancellationRequested) {
        var data = buffer.Take();
        handTracker.detect(data.DepthData, data.ColorData, data.Skeleton);
        fpsCounter.LogFPS();
      }
    }

    void CancelTracking() {
      if (cancellationTokenSource != null)
        cancellationTokenSource.Cancel();
    }

    void UpdateDisplay() {
      gesturesCanvas.Children.Clear();
      if (handTracker != null) {
        if (handTracker.PrevBoundingBox.Width > 0) {
          VisualUtil.DrawRectangle(gesturesCanvas, handTracker.PrevBoundingBox, Brushes.Red);
        }
        depthDisplayManager.UpdateBitmap(handTracker.SmoothedDepth.Bytes);
      } else if (displayDepth) {
        depthDisplayManager.UpdateBitmap();
      }
    }

    void kinectRuntime_AllFrameReady(object sender, AllFramesReadyEventArgs e) {
      if (replay != null && !replay.IsFinished)
        return;

      using (var cf = e.OpenColorImageFrame())
      using (var df = e.OpenDepthImageFrame())
      using (var sf = e.OpenSkeletonFrame()) {
        try {
          if (recorder != null) {
            recorder.Record(sf, df, cf);
          }
        } catch (ObjectDisposedException) { }

        if (cf != null)
          colorManager.Update(cf, !displayDepth);

        if (df != null) {
          depthFrameNumber = df.FrameNumber;
          depthDisplayManager.Update(df);
        }

        if (sf != null) {
          UpdateSkeletonDisplay(sf);
          if (buffer.Count <= 1)
            buffer.Add(new KinectDataPacket {
              ColorData = colorManager.PixelData,
              DepthData = depthDisplayManager.PixelData,
              Skeleton = SkeletonUtil.FirstTrackedSkeleton(sf.GetSkeletons())
            });
        }
      }
      UpdateDisplay();
    }

    void UpdateSkeletonDisplay(ReplaySkeletonFrame frame) {
      Dictionary<int, string> stabilities = new Dictionary<int, string>();
      foreach (var skeleton in frame.Skeletons) {
        if (skeleton.TrackingState != SkeletonTrackingState.Tracked)
          continue;

        contextTracker.Add(skeleton.Position.ToVector3(), skeleton.TrackingId);
        stabilities.Add(skeleton.TrackingId,
            contextTracker.IsStableRelativeToCurrentSpeed(skeleton.TrackingId) ? "Stable" : "Non stable");
      }

      try {
        skeletonDisplayManager.Draw(frame.Skeletons, seatedMode.IsChecked == true);
      } catch (Exception) {

      }

      stabilitiesList.ItemsSource = stabilities;
    }

    void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) {
      Clean();
    }

    void Clean() {
      CancelTracking();

      if (audioManager != null) {
        audioManager.Dispose();
        audioManager = null;
      }

      if (recorder != null) {
        recorder.Close();
        recorder = null;
      }

      if (eyeTracker != null) {
        eyeTracker.Dispose();
        eyeTracker = null;
      }

      if (kinectSensor != null) {
        kinectSensor.AllFramesReady -= kinectRuntime_AllFrameReady;
        kinectSensor.Stop();
        kinectSensor = null;
      }
    }

    void Button_Click(object sender, RoutedEventArgs e) {
      displayDepth = !displayDepth;

      if (displayDepth) {
        viewButton.Content = "View Color";
        kinectDisplay.DataContext = depthDisplayManager;
      } else {
        viewButton.Content = "View Depth";
        kinectDisplay.DataContext = colorManager;
      }
    }

    void nearMode_Checked_1(object sender, RoutedEventArgs e) {
      if (kinectSensor == null)
        return;

      kinectSensor.DepthStream.Range = DepthRange.Near;
      kinectSensor.SkeletonStream.EnableTrackingInNearRange = true;
    }

    void nearMode_Unchecked_1(object sender, RoutedEventArgs e) {
      if (kinectSensor == null)
        return;

      kinectSensor.DepthStream.Range = DepthRange.Default;
      kinectSensor.SkeletonStream.EnableTrackingInNearRange = false;
    }

    void seatedMode_Checked_1(object sender, RoutedEventArgs e) {
      if (kinectSensor == null)
        return;

      kinectSensor.SkeletonStream.TrackingMode = SkeletonTrackingMode.Seated;
    }

    void seatedMode_Unchecked_1(object sender, RoutedEventArgs e) {
      if (kinectSensor == null)
        return;

      kinectSensor.SkeletonStream.TrackingMode = SkeletonTrackingMode.Default;
    }

    void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
      switch (e.Key) {
        case Key.Space:
          RecordGesture();
          break;
        case Key.P:
          TogglePlay();
          break;
        case Key.T:
          StartTracking();
          break;
        default:
          break;
      }
    }
  }
}
