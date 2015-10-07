﻿/**
* PSMove API - A Unity5 plugin for the PSMove motion controller.
*              Derived from the psmove-ue4 plugin by Chadwick Boulay
*              and the UniMove plugin by the Copenhagen Game Collective
* Copyright (C) 2015, PolyarcGames (http://www.polyarcgames.com)
*                   Brendan Walker (brendan@polyarcgames.com)
* 
* All rights reserved.
*
* Redistribution and use in source and binary forms, with or without
* modification, are permitted provided that the following conditions are met:
*
*    1. Redistributions of source code must retain the above copyright
*       notice, this list of conditions and the following disclaimer.
*
*    2. Redistributions in binary form must reproduce the above copyright
*       notice, this list of conditions and the following disclaimer in the
*       documentation and/or other materials provided with the distribution.
*
* THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
* AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
* IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
* ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
* LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
* CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
* SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
* INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
* CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
* ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
* POSSIBILITY OF SUCH DAMAGE.
**/

#define LOAD_DLL_MANUALLY

using UnityEngine;
using System.Collections;
using System.Threading;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

public class PSMoveManager : MonoBehaviour 
{
    private IntPtr hidapiHandle;
    private IntPtr libusbHandle;
    private IntPtr opencvHandle;
    private IntPtr psmoveapiHandle;
    private IntPtr cleyeHandle;

    private static PSMoveManager ManagerInstance;

    public static PSMoveManager GetManagerInstance()
    {
        return ManagerInstance;
    }

    public void Awake() 
    {
#if LOAD_DLL_MANUALLY
        if (IntPtr.Size == 8)
        {
            hidapiHandle = LoadLib("Assets/Plugins/x86_64/hidapi.dll");
            libusbHandle = LoadLib("Assets/Plugins/x86_64/libusb-1.0.dll");
            opencvHandle = LoadLib("Assets/Plugins/x86_64/opencv_world300.dll");
            psmoveapiHandle = LoadLib("Assets/Plugins/x86_64/psmoveapi.dll");
        }
        else
        {
            hidapiHandle = LoadLib("Assets/Plugins/x86/hidapi.dll");
            cleyeHandle = LoadLib("c:/Windows/SysWOW64/CLEyeMulticam.dll");
            opencvHandle = LoadLib("Assets/Plugins/x86/opencv_world300.dll");
            psmoveapiHandle = LoadLib("Assets/Plugins/x86/psmoveapi.dll");
        }
#endif

        ManagerInstance = this;
	    PSMoveWorker.StartWorkerThread();
    }

    public void OnDestroy()
    {
        PSMoveWorker.StopWorkerThread();

        //Free any manually loaded DLLs
        if (psmoveapiHandle != IntPtr.Zero)
        {
            FreeLibrary(psmoveapiHandle);
        }

        if (opencvHandle != IntPtr.Zero)
        {
            FreeLibrary(opencvHandle);
        }

        if (libusbHandle != IntPtr.Zero)
        {
            FreeLibrary(libusbHandle);
        }

        if (hidapiHandle != IntPtr.Zero)
        {
            FreeLibrary(hidapiHandle);
        }

        if (cleyeHandle != IntPtr.Zero)
        {
            FreeLibrary(cleyeHandle);
        }

        ManagerInstance = null;
    }

    public void OnApplicationQuit()
    {
        PSMoveWorker.StopWorkerThread();
    }

    public PSMoveDataContext AcquirePSMove(int PSMoveID)
    {
        return PSMoveWorker.GetWorkerThreadInstance().AcquirePSMove(PSMoveID);
    }

    public void ReleasePSMove(PSMoveDataContext DataContext)
    {
        PSMoveWorker.GetWorkerThreadInstance().ReleasePSMove(DataContext);
    }

#if LOAD_DLL_MANUALLY
    private IntPtr LoadLib(string path)
    {
        IntPtr ptr = LoadLibrary(path);
        if (ptr == IntPtr.Zero)
        {
            int errorCode = Marshal.GetLastWin32Error();
            UnityEngine.Debug.LogError(string.Format("Failed to load library {1} (ErrorCode: {0})", errorCode, path));
        }
        else
        {
            UnityEngine.Debug.Log("loaded lib " + path);
        }
        return ptr;
    }
#endif

    // Win32 API
#if LOAD_DLL_MANUALLY
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string libname);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool FreeLibrary(IntPtr hModule);
#endif
}

// -- private definitions ----

// TrackingContext contains references to the psmoveapi tracker and fusion objects, the controllers,
// and references to the shared (controller independent) data and the controller(s) data.
class TrackingContext
{
    public static float CONTROLLER_COUNT_POLL_INTERVAL = 1000.0f; // milliseconds

    public PSMoveRawControllerData_TLS[] WorkerControllerDataArray;

    public IntPtr[] PSMoves; // Array of PSMove*
    public int PSMoveCount;
    public Stopwatch moveCountCheckTimer;

    public IntPtr PSMoveTracker; // PSMoveTracker*
    public int TrackerWidth;
    public int TrackerHeight;

    public IntPtr PSMoveFusion; // PSMoveFusion*
    
    // Constructor
    public TrackingContext(PSMoveRawControllerData_TLS[] controllerDataArray)
    {
        WorkerControllerDataArray= controllerDataArray;
        
        // This timestamp is used to throttle how frequently we poll for controller count changes
        moveCountCheckTimer = new Stopwatch();

        Reset();
    }
    
    public void Reset()
    {
        PSMoves = Enumerable.Repeat(IntPtr.Zero, PSMoveWorker.MAX_CONTROLLERS).ToArray();
        PSMoveCount = 0;
        moveCountCheckTimer.Reset();
        moveCountCheckTimer.Start();
        PSMoveTracker = IntPtr.Zero;
        TrackerWidth = 0;
        TrackerHeight = 0;
        PSMoveFusion = IntPtr.Zero;
    }
};

class PSMoveWorker
{
    public static int MAX_CONTROLLERS = 5; // 5 tracking colors available: magenta, cyan, yellow, red, blue

    public static PSMoveWorker GetWorkerThreadInstance()
    { 
        return WorkerInstance; 
    }

    public static void StartWorkerThread()
    {
        if (WorkerInstance == null)
        {
            WorkerInstance= new PSMoveWorker();
        }

        WorkerInstance.Start();
    }

    public static void StopWorkerThread()
    {
        if (WorkerInstance != null)
        {
            WorkerInstance.Stop();
        }
    }

    // Tell the PSMove Worker that we want to start listening to this controller.
    public PSMoveDataContext AcquirePSMove(int PSMoveID)
    {
        PSMoveDataContext DataContext= null;

        if (PSMoveID >= 0 && PSMoveID < MAX_CONTROLLERS)
        {
            // Bind the data context to the concurrent data for the requested controller
            // This doesn't mean  that the controller is active, just that a component
            // is now watching this block of data.
            // Also this is thread safe because were not actually looking at the concurrent data
            // at this point, just assigning a pointer to the concurrent data.
            DataContext= new PSMoveDataContext(
                PSMoveID,
                WorkerInstance.WorkerControllerDataArray_Concurrent[PSMoveID]);

            // The worker thread will create a tracker if one isn't active at this moment
            lock(this)
            {
                AcquiredContextCounter++;
            }
        }

        return DataContext;
    }

    public void ReleasePSMove(PSMoveDataContext DataContext)
    {
        if (DataContext.PSMoveID != -1)
        {
            DataContext.Clear();
        
            lock(this)
            {
                // The worker thread will tear-down the tracker
                //assert(AcquiredContextCounter > 0);
                AcquiredContextCounter--;
            }
        }
    }

    private PSMoveWorker()
    {
        WorkerInstance= this;

        StopSignal = new ManualResetEvent(false);
        ExitedSignal = new ManualResetEvent(false);
        WorkerThread = new Thread(() => { this.Run(); });

        WorkerControllerDataArray_Concurrent = new PSMoveRawControllerData_Concurrent[MAX_CONTROLLERS];
        WorkerControllerDataArray = new PSMoveRawControllerData_TLS[MAX_CONTROLLERS];
        for (int i= 0; i < WorkerControllerDataArray_Concurrent.Length; i++)
        {
            WorkerControllerDataArray_Concurrent[i] = new PSMoveRawControllerData_Concurrent();
            WorkerControllerDataArray[i] = new PSMoveRawControllerData_TLS(WorkerControllerDataArray_Concurrent[i]);
        }
    }
    
    private void Start()
    {
        if (!WorkerThread.IsAlive)
        {
            WorkerThread.Start();
        }
    }

    private void Stop()
    {
        // Signal the thread to stop
        StopSignal.Set();

        // Wait one second for the thread to finish
        ExitedSignal.WaitOne(1000);

        // Reset the stop and exited flags so that the thread can be restarted
        StopSignal.Reset();
        ExitedSignal.Reset();
    }

    private void Run()
    {
        try
        {
            bool receivedStopSignal = false;

            // Maintains the following psmove state on the stack
            // * psmove tracking state
            // * psmove fusion state
            // * psmove controller state
            // Tracking state is only initialized when we have a non-zero number of tracking contexts
            TrackingContext Context = new TrackingContext(WorkerControllerDataArray);

            if (PSMoveAPI.psmove_init(PSMoveAPI.PSMove_Version.PSMOVE_CURRENT_VERSION) == PSMove_Bool.PSMove_False)
            {
                throw new Exception("PS Move API init failed (wrong version?)");
            }
    
            //Initial wait before starting.
            Thread.Sleep(30);

            while (!receivedStopSignal)
            {
                int AcquiredContextCounter_TLS;

                lock(this)
                {
                    AcquiredContextCounter_TLS = this.AcquiredContextCounter;
                }

                // If there are component contexts active, make sure the tracking context is setup
                if (AcquiredContextCounter_TLS > 0 && !TrackingContextIsSetup(Context))
                {
                    TrackingContextSetup(Context);
                }
                // If there are no component contexts active, make sure the tracking context is torn-down
                else if (AcquiredContextCounter_TLS <= 0 && TrackingContextIsSetup(Context))
                {
                    TrackingContextTeardown(Context);
                }

                // Update controller state while tracking is active
                if (TrackingContextIsSetup(Context))
                {
                    // Setup or tear down controller connections based on the number of active controllers
                    TrackingContextUpdateControllerConnections(Context);

                    // Renew the image on camera
                    using(new PSMoveHitchWatchdog("FPSMoveWorker_UpdateImage", 30*PSMoveHitchWatchdog.MICROSECONDS_PER_MILLISECOND))
                    {
                        PSMoveAPI.psmove_tracker_update_image(Context.PSMoveTracker); // Sometimes libusb crashes here.
                    }
            
                    // Update the raw positions on the local controller data
                    for (int psmove_id = 0; psmove_id < Context.PSMoveCount; psmove_id++)
                    {
                        PSMoveRawControllerData_TLS localControllerData = WorkerControllerDataArray[psmove_id];
                                
                        ControllerUpdatePositions(
                            Context.PSMoveTracker,
                            Context.PSMoveFusion,
                            Context.PSMoves[psmove_id],
                            localControllerData);
                    }
            
                    // Do bluetooth IO: Orientation, Buttons, Rumble
                    for (int psmove_id = 0; psmove_id < Context.PSMoveCount; psmove_id++)
                    {
                        //TODO: Is it necessary to keep polling until no frames are left?
                        while (PSMoveAPI.psmove_poll(Context.PSMoves[psmove_id]) > 0)
                        {
                            PSMoveRawControllerData_TLS localControllerData = WorkerControllerDataArray[psmove_id];
                        
                            // Update the controller status (via bluetooth)
                            PSMoveAPI.psmove_poll(Context.PSMoves[psmove_id]);  // Necessary to poll yet again?
                        
                            // Store the controller orientation
                            ControllerUpdateOrientations(Context.PSMoves[psmove_id], localControllerData);
                        
                            // Store the button state
                            ControllerUpdateButtonState(Context.PSMoves[psmove_id], localControllerData);

                            // Now read in requested changes from Component. e.g., RumbleRequest, ResetPoseRequest, CycleColourRequest
                            localControllerData.WorkerRead();
                        
                            // Set the controller rumble (uint8; 0-255)
                            PSMoveAPI.psmove_set_rumble(Context.PSMoves[psmove_id], (char)localControllerData.RumbleRequest);
                        
                            // See if the reset pose request has been posted by the component.
                            // It is not recommended to use this. We will soon expose a psmove_reset_yaw function that should be used instead.
                            // Until then, use the local yaw reset in the psmove component.
                            if (localControllerData.ResetPoseRequest)
                            {
                                UnityEngine.Debug.Log("PSMoveWorker:: RESET POSE");

                                PSMoveAPI.psmove_reset_orientation(Context.PSMoves[psmove_id]);
                                PSMoveAPI.psmove_tracker_reset_location(Context.PSMoveTracker, Context.PSMoves[psmove_id]);
                            
                                // Clear the request flag now that we've handled the request
                                localControllerData.ResetPoseRequest = false;
                            }

                            if (localControllerData.CycleColourRequest)
                            {
                                UnityEngine.Debug.Log("PSMoveWorker:: CYCLE COLOUR");
                                PSMoveAPI.psmove_tracker_cycle_color(Context.PSMoveTracker, Context.PSMoves[psmove_id]);
                                localControllerData.CycleColourRequest = false;
                            }

                            // Publish the worker data to the component. e.g., Position, Orientation, Buttons
                            // This also publishes updated ResetPoseRequest and CycleColourRequest.
                            localControllerData.WorkerPost();
                        }
                    }
                }

                // See if the main thread signaled us to stop
                if (StopSignal.WaitOne(0))
                {
                    receivedStopSignal = true;
                }
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError(string.Format("PSMoveWorker: WorkerThread crashed: {0}", e.Message));
            UnityEngine.Debug.LogException(e);
        }
        finally
        {
            ExitedSignal.Set();
        }
    }

    #region Private Tracking Context Methods
    private static bool TrackingContextSetup(TrackingContext context)
    {
        bool success = true;

        // Clear out the tracking state
        // Reset the shared worker data
        context.Reset();

        UnityEngine.Debug.Log("Setting up PSMove Tracking Context");

        // Initialize and configure the psmove_tracker.
        {
            PSMoveAPI.PSMoveTrackerSettings settings = new PSMoveAPI.PSMoveTrackerSettings();
            PSMoveAPI.psmove_tracker_settings_set_default(ref settings);
            settings.color_mapping_max_age = 0; // Don't used cached color mapping file
            settings.exposure_mode = PSMoveTracker_Exposure.Exposure_LOW;
            settings.use_fitEllipse = 1;
            settings.camera_mirror = PSMove_Bool.PSMove_True;

            context.PSMoveTracker = PSMoveAPI.psmove_tracker_new_with_settings(ref settings);
        }

        if (context.PSMoveTracker != IntPtr.Zero)
        {
            UnityEngine.Debug.Log("PSMove tracker initialized.");

            PSMoveAPI.PSMoveTrackerSmoothingSettings smoothing_settings = new PSMoveAPI.PSMoveTrackerSmoothingSettings();
            PSMoveAPI.psmove_tracker_get_smoothing_settings(context.PSMoveTracker, ref smoothing_settings);
            smoothing_settings.filter_do_2d_r = 0;
            smoothing_settings.filter_do_2d_xy = 0;
            smoothing_settings.filter_3d_type = PSMoveTracker_Smoothing_Type.Smoothing_Kalman;
            PSMoveAPI.psmove_tracker_set_smoothing_settings(context.PSMoveTracker, ref smoothing_settings);

            PSMoveAPI.psmove_tracker_get_size(context.PSMoveTracker, ref context.TrackerWidth, ref context.TrackerHeight);
            UnityEngine.Debug.Log(string.Format("Camera Dimensions: {0} x {1}", context.TrackerWidth, context.TrackerHeight));
        }
        else
        {
            UnityEngine.Debug.LogError("PSMove tracker failed to initialize.");
            success = false;
        }

        // Initialize fusion API if the tracker started
        if (success)
        {
            context.PSMoveFusion = PSMoveAPI.psmove_fusion_new(context.PSMoveTracker, 1.0f, 1000.0f);

            if (context.PSMoveFusion != IntPtr.Zero)
            {
                UnityEngine.Debug.Log("PSMove fusion initialized.");
            }
            else
            {
                UnityEngine.Debug.LogError("PSMove failed to initialize.");
                success = false;
            }
        }

        if (!success)
        {
            TrackingContextTeardown(context);
        }

        return success;
    }

    private static bool TrackingContextIsSetup(TrackingContext context)
    {
        return context.PSMoveTracker != IntPtr.Zero && context.PSMoveFusion != IntPtr.Zero;
    }

    private static bool TrackingContextUpdateControllerConnections(TrackingContext context)
    {
        bool controllerCountChanged = false;
        System.Diagnostics.Debug.Assert(TrackingContextIsSetup(context));
    
        if (context.moveCountCheckTimer.ElapsedMilliseconds >= TrackingContext.CONTROLLER_COUNT_POLL_INTERVAL)
        {
            // Update the number
            int newcount = PSMoveAPI.psmove_count_connected();
        
            if (context.PSMoveCount != newcount)
            {
                UnityEngine.Debug.Log(string.Format("PSMove Controllers count changed: {0} -> {1}.", context.PSMoveCount, newcount));
            
                context.PSMoveCount = newcount;
                controllerCountChanged = true;
            }
        
            // Refresh the connection and tracking state of every controller entry
            for (int psmove_id = 0; psmove_id < context.PSMoves.Length; psmove_id++)
            {
                if (psmove_id < context.PSMoveCount)
                {
                    if (context.PSMoves[psmove_id] == IntPtr.Zero)
                    {
                        // The controller should be connected
                        context.PSMoves[psmove_id] = PSMoveAPI.psmove_connect_by_id(psmove_id);

                        if (context.PSMoves[psmove_id] != IntPtr.Zero)
                        {
                            PSMoveAPI.psmove_enable_orientation(context.PSMoves[psmove_id], PSMove_Bool.PSMove_True);
                            System.Diagnostics.Debug.Assert(PSMoveAPI.psmove_has_orientation(context.PSMoves[psmove_id]) == PSMove_Bool.PSMove_True);

                            context.WorkerControllerDataArray[psmove_id].IsConnected = true;
                        }
                        else
                        {
                            context.WorkerControllerDataArray[psmove_id].IsConnected = false;
                            UnityEngine.Debug.LogError(string.Format("Failed to connect to PSMove controller {0}", psmove_id));
                        }
                    }

                    if (context.PSMoves[psmove_id] != IntPtr.Zero && 
                        context.WorkerControllerDataArray[psmove_id].IsEnabled == false)
                    {
                        // The controller is connected, but not tracking yet
                        // Enable tracking for this controller with next available color.
                        if (PSMoveAPI.psmove_tracker_enable(
                                context.PSMoveTracker, 
                                context.PSMoves[psmove_id]) == PSMoveTracker_Status.Tracker_CALIBRATED)
                        {
                            context.WorkerControllerDataArray[psmove_id].IsEnabled = true;
                        }
                        else
                        {
                            UnityEngine.Debug.LogError(string.Format("Failed to enable tracking for PSMove controller {0}", psmove_id));
                        }
                    }
                }
                else
                {
                    // The controller should no longer be tracked
                    if (context.PSMoves[psmove_id] != IntPtr.Zero)
                    {
                        PSMoveAPI.psmove_disconnect(context.PSMoves[psmove_id]);
                        context.PSMoves[psmove_id] = IntPtr.Zero;
                        context.WorkerControllerDataArray[psmove_id].IsEnabled = false;
                        context.WorkerControllerDataArray[psmove_id].IsConnected = false;
                    }
                }
            }
        
            // Remember the last time we polled the move count
            context.moveCountCheckTimer.Reset();
            context.moveCountCheckTimer.Start();
        }
    
        return controllerCountChanged;
    }

    private static void TrackingContextTeardown(TrackingContext context)
    {
        UnityEngine.Debug.Log("Tearing down PSMove Tracking Context");
    
        // Delete the controllers
        for (int psmove_id = 0; psmove_id < context.PSMoves.Length; psmove_id++)
        {
            if (context.PSMoves[psmove_id] != IntPtr.Zero)
            {
                UnityEngine.Debug.Log(string.Format("Disconnecting PSMove controller {0}", psmove_id));
                context.WorkerControllerDataArray[psmove_id].IsConnected = false;
                context.WorkerControllerDataArray[psmove_id].IsEnabled = false;
                PSMoveAPI.psmove_disconnect(context.PSMoves[psmove_id]);
                context.PSMoves[psmove_id] = IntPtr.Zero;
            }
        }
    
        // Delete the tracking fusion state
        if (context.PSMoveFusion != IntPtr.Zero)
        {
            UnityEngine.Debug.Log("PSMove fusion disposed");
            PSMoveAPI.psmove_fusion_free(context.PSMoveFusion);
            context.PSMoveFusion = IntPtr.Zero;
        }
    
        // Delete the tracker state
        if (context.PSMoveTracker != IntPtr.Zero)
        {
            UnityEngine.Debug.Log("PSMove tracker disposed");
            PSMoveAPI.psmove_tracker_free(context.PSMoveTracker);
            context.PSMoveTracker = IntPtr.Zero;
        }
    
        context.Reset();
    }

    private static void ControllerUpdatePositions(
        IntPtr psmove_tracker, // PSMoveTracker*
        IntPtr psmove_fusion, // PSMoveFusion*
        IntPtr psmove, // PSMove*
        PSMoveRawControllerData_Base controllerData)
    {
        // Find the sphere position in the camera
        PSMoveAPI.psmove_tracker_update(psmove_tracker, psmove);
    
        PSMoveTracker_Status curr_status = 
            PSMoveAPI.psmove_tracker_get_status(psmove_tracker, psmove);
    
        // Can we actually see the controller this frame?
        controllerData.IsTracking = curr_status == PSMoveTracker_Status.Tracker_TRACKING;

        // Update the position of the controller
        if (controllerData.IsTracking)
        {        
            float xcm= 0.0f, ycm = 0.0f, zcm = 0.0f;

            PSMoveAPI.psmove_fusion_get_transformed_location(psmove_fusion, psmove, ref xcm, ref ycm, ref zcm);
        
            // [Store the controller position]
            // Remember the position the ps move controller in either its native space
            // or in a transformed space if a transform file existed.
            controllerData.PSMovePosition = new Vector3(xcm, ycm, zcm);
        }
    }

    private static void ControllerUpdateOrientations(
        IntPtr psmove, // PSMove*
        PSMoveRawControllerData_Base controllerData)
    {
        float oriw = 1.0f, orix = 0.0f, oriy = 0.0f, oriz = 0.0f;

        // Get the controller orientation (uses IMU).
        PSMoveAPI.psmove_get_orientation(psmove, ref oriw, ref orix, ref oriy, ref oriz);

        //NOTE: This orientation is in the PSMoveApi coordinate system
        controllerData.PSMoveOrientation = new Quaternion(orix, oriy, oriz, oriw);
    }

    private static void ControllerUpdateButtonState(
        IntPtr psmove, // PSMove*
        PSMoveRawControllerData_Base controllerData)
    {
        // Get the controller button state
        controllerData.Buttons = PSMoveAPI.psmove_get_buttons(psmove);  // Bitwise; tells if each button is down.

        // Get the controller trigger value (uint8; 0-255)
        controllerData.TriggerValue = (byte)PSMoveAPI.psmove_get_trigger(psmove);
    }
    #endregion

    private static PSMoveWorker WorkerInstance;

    // Number of active data contexts
    private int AcquiredContextCounter;

    // Number of controllers currently active
    private int PSMoveCount;

    // Published worker data that shouldn't touch directly.
    // Access through _TLS version of the structures.
    private PSMoveRawControllerData_Concurrent[] WorkerControllerDataArray_Concurrent;

    // Thread local version of the concurrent controller and shared data
    private PSMoveRawControllerData_TLS[] WorkerControllerDataArray;

    // Threading State
    private Thread WorkerThread;
    private ManualResetEvent StopSignal;
    private ManualResetEvent ExitedSignal;
}