﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Debugger.Interop;
using Debugger.Interop.CorDebug;
using Microsoft.Win32;

namespace Debugger
{
	/// <summary>
	/// A base class for all classes declared by the debugger
	/// </summary>
	public class DebuggerObject: MarshalByRefObject
	{
		
	}
	
	public class NDebugger: DebuggerObject
	{
		ICorDebug                  corDebug;
		ManagedCallbackSwitch      managedCallbackSwitch;
		ManagedCallbackProxy       managedCallbackProxy;
		
		internal List<Breakpoint> breakpoints = new List<Breakpoint>();
		internal List<Process> processes = new List<Process>();
		
		MTA2STA mta2sta = new MTA2STA();
		
		string debuggeeVersion;
		
		Options options = new Options();
		
		public MTA2STA MTA2STA {
			get {
				return mta2sta;
			}
		}
		
		internal ICorDebug CorDebug {
			get {
				return corDebug;
			}
		}
		
		public string DebuggeeVersion {
			get {
				return debuggeeVersion;
			}
		}
		
		public Options Options {
			get { return options; }
			set { options = value; }
		}
		
		public IEnumerable<Breakpoint> Breakpoints {
			get { return this.breakpoints; }
		}
		
		public IEnumerable<Process> Processes {
			get { return this.processes; }
		}
		
		public NDebugger()
		{
			if (ApartmentState.STA == System.Threading.Thread.CurrentThread.GetApartmentState()) {
				mta2sta.CallMethod = CallMethod.HiddenFormWithTimeout;
			} else {
				mta2sta.CallMethod = CallMethod.DirectCall;
			}
		}
		
		internal Process GetProcess(ICorDebugProcess corProcess) {
			foreach (Process process in this.Processes) {
				if (process.CorProcess == corProcess) {
					return process;
				}
			}
			return null;
		}
		
		/// <summary>
		/// Get the .NET version of the process that called this function
		/// </summary>
		public string GetDebuggerVersion()
		{
			int size;
			NativeMethods.GetCORVersion(null, 0, out size);
			StringBuilder sb = new StringBuilder(size);
			int hr = NativeMethods.GetCORVersion(sb, sb.Capacity, out size);
			return sb.ToString();
		}
		
		/// <summary>
		/// Get the .NET version of a given program - eg. "v1.1.4322"
		/// </summary>
		/// <remarks> Returns empty string for unmanaged applications </remarks>
		public string GetProgramVersion(string exeFilename)
		{
			int size;
			NativeMethods.GetRequestedRuntimeVersion(exeFilename, null, 0, out size);
			StringBuilder sb = new StringBuilder(size);
			NativeMethods.GetRequestedRuntimeVersion(exeFilename, sb, sb.Capacity, out size);
			sb.Length = size;
			return sb.ToString().TrimEnd('\0');
		}
		
		/// <summary>
		/// Prepares the debugger
		/// </summary>
		/// <param name="debuggeeVersion">Version of the program to debug - eg. "v1.1.4322"
		/// If null, the version of the executing process will be used</param>
		internal void InitDebugger(string debuggeeVersion)
		{
			if (IsKernelDebuggerEnabled) {
				throw new DebuggerException("Can not debug because kernel debugger is enabled");
			}
			if (string.IsNullOrEmpty(debuggeeVersion)) {
				debuggeeVersion = GetDebuggerVersion();
				TraceMessage("Debuggee version: Unknown (assuming " + debuggeeVersion + ")");
			} else {
				TraceMessage("Debuggee version: " + debuggeeVersion);
			}
			this.debuggeeVersion = debuggeeVersion;
			
			int debuggerVersion;
			// The CLR does not provide 4.0 debugger interface for older versions
			if (debuggeeVersion.StartsWith("v1") || debuggeeVersion.StartsWith("v2")) {
				debuggerVersion = 3; // 2.0 CLR
				TraceMessage("Debugger interface version: v2.0");
			} else {
				debuggerVersion = 4; // 4.0 CLR
				TraceMessage("Debugger interface version: v4.0");
			}
			
			corDebug = NativeMethods.CreateDebuggingInterfaceFromVersion(debuggerVersion, debuggeeVersion);
			TrackedComObjects.Track(corDebug);
			
			managedCallbackSwitch = new ManagedCallbackSwitch(this);
			managedCallbackProxy = new ManagedCallbackProxy(this, managedCallbackSwitch);
			
			corDebug.Initialize();
			corDebug.SetManagedHandler(managedCallbackProxy);
			
			TraceMessage("ICorDebug initialized");
		}
		
		internal void TerminateDebugger()
		{
			foreach(Breakpoint breakpoint in this.Breakpoints) {
				breakpoint.NotifyDebuggerTerminated();
			}
			
			corDebug.Terminate();
			TraceMessage("ICorDebug terminated");
			
			int released = TrackedComObjects.ReleaseAll();
			TraceMessage("Released " + released + " tracked COM objects");
		}
		
		public Breakpoint AddBreakpoint(string fileName, int line, int column = 0, bool enabled = true)
		{
			Breakpoint breakpoint = new Breakpoint(fileName, line, column, enabled);
			AddBreakpoint(breakpoint);
			return breakpoint;
		}
		
//		public ILBreakpoint AddILBreakpoint(string typeName, int line, int metadataToken, int memberToken, int offset, bool enabled)
//		{
//			ILBreakpoint breakpoint = new ILBreakpoint(typeName, line, metadataToken, memberToken, offset, enabled);
//			AddBreakpoint(breakpoint);
//			return breakpoint;
//		}
		
		void AddBreakpoint(Breakpoint breakpoint)
		{
			this.breakpoints.Add(breakpoint);
			
			foreach (Process process in this.Processes) {
				foreach(Module module in process.Modules) {
					breakpoint.SetBreakpoint(module);
				}				
			}
		}
		
		public void RemoveBreakpoint(Breakpoint breakpoint)
		{
			breakpoint.IsEnabled = false;
			this.breakpoints.Remove(breakpoint);
		}
		
		internal Breakpoint GetBreakpoint(ICorDebugBreakpoint corBreakpoint)
		{
			foreach (Breakpoint breakpoint in this.Breakpoints) {
				if (breakpoint.IsOwnerOf(corBreakpoint)) {
					return breakpoint;
				}
			}
			return null;
		}
		
		internal void TraceMessage(string message)
		{
			message = "Debugger: " + message;
			System.Console.WriteLine(message);
			System.Diagnostics.Debug.WriteLine(message);
		}
		
		public void StartWithoutDebugging(System.Diagnostics.ProcessStartInfo psi)
		{
			System.Diagnostics.Process process;
			process = new System.Diagnostics.Process();
			process.StartInfo = psi;
			process.Start();
		}
		
		internal object ProcessIsBeingCreatedLock = new object();
		
		public Process Start(string filename, string workingDirectory, string arguments, bool breakInMain)
		{
			InitDebugger(GetProgramVersion(filename));
			lock(ProcessIsBeingCreatedLock) {
				Process process = Process.CreateProcess(this, filename, workingDirectory, arguments);
				// Expose a race conditon
				System.Threading.Thread.Sleep(0);
				process.BreakInMain = breakInMain;
				this.processes.Add(process);
				return process;
			}
		}
		
		public Process Attach(System.Diagnostics.Process existingProcess)
		{
			string mainModule = existingProcess.MainModule.FileName;
			InitDebugger(GetProgramVersion(mainModule));
			lock(ProcessIsBeingCreatedLock) {
				ICorDebugProcess corDebugProcess = corDebug.DebugActiveProcess((uint)existingProcess.Id, 0);
				// TODO: Can we get the acutal working directory?
				Process process = new Process(this, corDebugProcess, Path.GetDirectoryName(mainModule));
				this.processes.Add(process);
				return process;
			}
		}
		
		public void Detach()
		{
			// Detach all processes.
			foreach(Process process in this.Processes) {
				if (process == null || process.HasExited) 
					continue;
				process.Detach();
			}
		}
		
		public bool IsKernelDebuggerEnabled {
			get {
				string systemStartOptions = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\").GetValue("SystemStartOptions", string.Empty).ToString();
				// XP does not have the slash, Vista does have it
				systemStartOptions = ("/" + systemStartOptions).ToLower().Replace(" ", " /");
				if (systemStartOptions.Contains("/nodebug")) {
					// this option overrides the others
					return false;
				}
				if (systemStartOptions.Contains("/debug") || 
				    systemStartOptions.Contains("/crashdebug") || 
				    systemStartOptions.Contains("/debugport") || 
				    systemStartOptions.Contains("/baudrate")) {
					return true;
				} else {
					return false;
				}
			}
		}
		
		/// <summary> Try to load module symbols using the search path defined in the options </summary>
		public void ReloadModuleSymbols()
		{
			foreach(Process process in this.Processes) {
				foreach(Module module in process.Modules) {
					module.LoadSymbolsFromDisk(process.Options.SymbolsSearchPaths);
				}
			}
			TraceMessage("Reloaded symbols");
		}
		
		/// <summary> Reset the just my code status of modules.  Use this after changing any stepping options. </summary>
		public void ResetJustMyCodeStatus()
		{
			foreach(Process process in this.Processes) {
				foreach(Module module in process.Modules) {
					module.ResetJustMyCodeStatus();
				}
			}
			TraceMessage("Just my code reseted");
		}
	}
	
	[Serializable]
	public class DebuggerEventArgs: EventArgs
	{
		/// <summary> The process on which the event occured.  Can be null. </summary>
		public Process Process { get; set; }
	}
	
	/// <summary>
	/// This event occurs when the debuggee stops.
	/// Note that several events can happen at the same time.
	/// </summary>
	[Serializable]
	public class DebuggerPausedEventArgs: DebuggerEventArgs
	{
		/// <summary> The thread on which the event occured.  Can be null if the event was not thread specific. </summary>
		public Thread Thread { get; set; }
		
		/// <summary> Breakpoints hit </summary>
		public List<Breakpoint> BreakpointsHit { get; set; }
		
		/// <summary> Exception thrown </summary>
		public Exception ExceptionThrown { get; set; }
		
		/// <summary> Break, stepper or any other pause reason. </summary>
		public bool Break { get; set; }
	}
	
	[Serializable]
	public class ModuleEventArgs: DebuggerEventArgs
	{
		public Module Module { get; private set; }
		
		public ModuleEventArgs(Module module)
		{
			this.Process = module.Process;
			this.Module = module;
		}
	}
	
	[Serializable]
	public class MessageEventArgs : EventArgs
	{
		public Process Process { get; private set; }
		public int Level { get; private set; }
		public string Message { get; private set; }
		public string Category { get; private set; }
		
		public MessageEventArgs(Process process, string message)
		{
			this.Process = process;
			this.Message = message;
		}
		
		public MessageEventArgs(Process process, int level, string message, string category)
		{
			this.Process = process;
			this.Level = level;
			this.Message = message;
			this.Category = category;
		}
	}
}
