﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Debugger.Interop.CorDebug;
using Debugger.Interop.CorSym;

namespace Debugger
{
	internal enum DebuggeeStateAction { Keep, Clear }
	
	/// <summary>
	/// Debug Mode Flags.
	/// </summary>
	public enum DebugModeFlag
	{
		/// <summary>
		/// Run in the same mode as without debugger.
		/// </summary>
		Default,
		/// <summary>
		/// Run in forced optimized mode.
		/// </summary>
		Optimized,
		/// <summary>
		/// Run in debug mode (easy inspection) but slower.
		/// </summary>
		Debug,
		/// <summary>
		/// Run in ENC mode (ENC possible) but even slower than debug
		/// </summary>
		Enc
	}
	
	public class Process: DebuggerObject
	{
		NDebugger debugger;
		
		ICorDebugProcess corProcess;
		ManagedCallback callbackInterface;
		
		List<ICorDebugFunctionBreakpoint> tempBreakpoints = new List<ICorDebugFunctionBreakpoint>();
		
		internal List<Eval> activeEvals = new List<Eval>();
		internal List<Module> modules = new List<Module>();
		internal List<Thread> threads = new List<Thread>();
		internal List<AppDomain> appDomains = new List<AppDomain>();
		
		string workingDirectory;
		
		public event EventHandler<MessageEventArgs> LogMessage;
		public event EventHandler<ModuleEventArgs> ModuleLoaded;
		public event EventHandler<ModuleEventArgs> ModuleUnloaded;
		public event EventHandler<DebuggerPausedEventArgs> Paused;
		public event EventHandler<DebuggerEventArgs> Resumed;
		public event EventHandler<DebuggerEventArgs> Exited;
		
		public NDebugger Debugger {
			get { return debugger; }
		}
		
		internal ICorDebugProcess CorProcess {
			get { return corProcess; }
		}
		
		public Options Options {
			get { return debugger.Options; }
		}
		
		public string DebuggeeVersion {
			get { return debugger.DebuggeeVersion; }
		}
		
		internal ManagedCallback CallbackInterface {
			get { return callbackInterface; }
		}
		
		internal bool Evaluating {
			get { return activeEvals.Count > 0; }
		}
		
		public IEnumerable<Module> Modules {
			get { return this.modules; }
		}
		
		public IEnumerable<Thread> Threads {
			get { return this.threads; }
		}
		
		internal bool BreakInMain { get; set; }
		
		public IEnumerable<AppDomain> AppDomains {
			get { return appDomains; }
		}
		
		List<Stepper> steppers = new List<Stepper>();
		
		internal List<Stepper> Steppers {
			get { return steppers; }
		}
		
		public string WorkingDirectory {
			get { return workingDirectory; }
		}
		
		public static DebugModeFlag DebugMode { get; set; }
		
		internal Process(NDebugger debugger, ICorDebugProcess corProcess, string workingDirectory)
		{
			this.debugger = debugger;
			this.corProcess = corProcess;
			this.workingDirectory = workingDirectory;
			
			this.callbackInterface = new ManagedCallback(this);
		}
		
		static unsafe public Process CreateProcess(NDebugger debugger, string filename, string workingDirectory, string arguments)
		{
			debugger.TraceMessage("Executing " + filename + " " + arguments);
			
			uint[] processStartupInfo = new uint[17];
			processStartupInfo[0] = sizeof(uint) * 17;
			uint[] processInfo = new uint[4];
			
			ICorDebugProcess outProcess;
			
			if (workingDirectory == null || workingDirectory == "") {
				workingDirectory = System.IO.Path.GetDirectoryName(filename);
			}
			
			_SECURITY_ATTRIBUTES secAttr = new _SECURITY_ATTRIBUTES();
			secAttr.bInheritHandle = 0;
			secAttr.lpSecurityDescriptor = IntPtr.Zero;
			secAttr.nLength = (uint)sizeof(_SECURITY_ATTRIBUTES);
			
			fixed (uint* pprocessStartupInfo = processStartupInfo)
				fixed (uint* pprocessInfo = processInfo)
				outProcess =
				debugger.CorDebug.CreateProcess(
					filename,   // lpApplicationName
					// If we do not prepend " ", the first argument migh just get lost
					" " + arguments,                       // lpCommandLine
					ref secAttr,                       // lpProcessAttributes
					ref secAttr,                      // lpThreadAttributes
					1,//TRUE                    // bInheritHandles
					0x00000010 /*CREATE_NEW_CONSOLE*/,    // dwCreationFlags
					IntPtr.Zero,                       // lpEnvironment
					workingDirectory,                       // lpCurrentDirectory
					(uint)pprocessStartupInfo,        // lpStartupInfo
					(uint)pprocessInfo,               // lpProcessInformation,
					CorDebugCreateProcessFlags.DEBUG_NO_SPECIAL_OPTIONS   // debuggingFlags
				);
			
			return new Process(debugger, outProcess, workingDirectory);
		}
		
		internal void OnLogMessage(MessageEventArgs arg)
		{
			if (this.LogMessage != null) {
				this.LogMessage(this, arg);
			}
		}
		
		public void TraceMessage(string message, params object[] args)
		{
			if (args.Length > 0)
				message = string.Format(message, args);
			this.Debugger.TraceMessage(message);
		}
		
		internal AppDomain GetAppDomain(ICorDebugAppDomain corAppDomain)
		{
			foreach(AppDomain a in this.AppDomains) {
				if (a.CorAppDomain.Equals(corAppDomain)) {
					return a;
				}
			}
			throw new DebuggerException("AppDomain not found");
		}
		
		internal Eval GetActiveEval(ICorDebugEval corEval)
		{
			foreach(Eval eval in this.activeEvals) {
				if (eval.CorEval == corEval) {
					return eval;
				}
			}
			throw new DebuggerException("Eval not found for given ICorDebugEval");
		}
		
		public Module GetModule(string filename)
		{
			foreach(Module module in this.Modules) {
				if (module.Name == filename) {
					return module;
				}
			}
			throw new DebuggerException("Module \"" + filename + "\" is not in collection");
		}

		internal Module GetModule(ICorDebugModule corModule)
		{
			foreach(Module module in this.Modules) {
				if (module.CorModule == corModule) {
					return module;
				}
			}
			throw new DebuggerException("Module is not in collection");
		}
		
		internal Thread GetThread(ICorDebugThread corThread)
		{
			foreach(Thread thread in this.Threads) {
				if (thread.CorThread == corThread) {
					return thread;
				}
			}
			// Sometimes, the thread is not reported for some unkown reason
			TraceMessage("Thread not found in collection");
			Thread newThread = new Thread(this, corThread);
			this.threads.Add(newThread);
			return newThread;
		}
		
		/// <summary> Read the specified amount of memory at the given memory address </summary>
		/// <returns> The content of the memory.  The amount of the read memory may be less then requested. </returns>
		public unsafe byte[] ReadMemory(ulong address, int size)
		{
			byte[] buffer = new byte[size];
			int readCount;
			fixed(byte* pBuffer = buffer) {
				readCount = (int)corProcess.ReadMemory(address, (uint)size, new IntPtr(pBuffer));
			}
			if (readCount != size) Array.Resize(ref buffer, readCount);
			return buffer;
		}
		
		/// <summary> Writes the given buffer at the specified memory address </summary>
		/// <returns> The number of bytes written </returns>
		public unsafe int WriteMemory(ulong address, byte[] buffer)
		{
			if (buffer.Length == 0) return 0;
			int written;
			fixed(byte* pBuffer = buffer) {
				written = (int)corProcess.WriteMemory(address, (uint)buffer.Length, new IntPtr(pBuffer));
			}
			return written;
		}
		
		// State control for the process
		
		internal bool TerminateCommandIssued = false;
		
		#region Events
		
		internal void OnPaused(DebuggerPausedEventArgs e)
		{
			AssertPaused();
			DisableAllSteppers();
			
			foreach (var corBreakpoint in tempBreakpoints) {
				corBreakpoint.Activate(0);
			}
			tempBreakpoints.Clear();
			
			// No real purpose - just additional check
			if (callbackInterface.IsInCallback) throw new DebuggerException("Can not raise event within callback.");
			TraceMessage ("Debugger event: OnPaused()");
			if (Paused != null) {
				foreach(EventHandler<DebuggerPausedEventArgs> d in Paused.GetInvocationList()) {
					if (IsRunning) {
						TraceMessage ("Skipping OnPaused delegate because process has resumed");
						break;
					}
					if (this.TerminateCommandIssued || this.HasExited) {
						TraceMessage ("Skipping OnPaused delegate because process has exited");
						break;
					}
					d(this, e);
				}
			}
		}
		
		void OnResumed()
		{
			AssertRunning();
			if (callbackInterface.IsInCallback)
				throw new DebuggerException("Can not raise event within callback.");
			TraceMessage ("Debugger event: OnResumed()");
			if (Resumed != null) {
				Resumed(this, new DebuggerEventArgs() { Process = this });
			}
		}
		
		#endregion
		
		#region PauseSession & DebugeeState
		
		long pauseSession;
		long nextPauseSession = 1;
		long debuggeeState;
		long nextDebuggeeState = 1;
		
		/// <summary>
		/// Indentification of the current debugger session. This value changes whenever debugger is continued
		/// </summary>
		public long PauseSession {
			get { return pauseSession; }
		}
		
		/// <summary>
		/// Indentification of the state of the debugee. This value changes whenever the state of the debugee significatntly changes
		/// </summary>
		public long DebuggeeState {
			get { return debuggeeState; }
		}
		
		/// <summary> Puts the process into a paused state </summary>
		internal void NotifyPaused()
		{
			AssertRunning();
			pauseSession = nextPauseSession++;
			if (debuggeeState == 0) {
				debuggeeState = nextDebuggeeState++;
			}
		}
		
		/// <summary> Puts the process into a resumed state </summary>
		internal void NotifyResumed(DebuggeeStateAction action)
		{
			AssertPaused();
			pauseSession = 0;
			if (action == DebuggeeStateAction.Clear) {
				if (debuggeeState == 0) throw new DebuggerException("Debugee state already cleared");
				debuggeeState = 0;
			}
		}
		
		#endregion
		
		internal void AssertPaused()
		{
			if (IsRunning) {
				throw new DebuggerException("Process is not paused.");
			}
		}
		
		internal void AssertRunning()
		{
			if (IsPaused) {
				throw new DebuggerException("Process is not running.");
			}
		}
		
		public bool IsRunning {
			get { return pauseSession == 0; }
		}
		
		public uint Id {
			get { return corProcess.GetID(); }
		}
		
		public bool IsPaused {
			get { return !IsRunning; }
		}
		
		bool hasExited = false;
		
		public bool HasExited {
			get {
				return hasExited;
			}
		}
		
		internal void OnExited()
		{
			if(!hasExited) {
				hasExited = true;
				if (Exited != null) {
					Exited(this, new DebuggerEventArgs() { Process = this });
				}
				// Expire pause seesion first
				if (IsPaused) {
					NotifyResumed(DebuggeeStateAction.Clear);
				}
				debugger.processes.Remove(this);
				
				if (debugger.processes.Count == 0) {
					// Exit callback and then terminate the debugger
					this.Debugger.MTA2STA.AsyncCall( delegate { this.Debugger.TerminateDebugger(); } );
				}
			}
		}
		
		public void Break()
		{
			AssertRunning();
			
			corProcess.Stop(uint.MaxValue); // Infinite; ignored anyway
			
			NotifyPaused();
			OnPaused(new DebuggerPausedEventArgs() { Process = this });
		}
		
		public void Detach()
		{
			if (IsRunning) {
				corProcess.Stop(uint.MaxValue);
				NotifyPaused();
			}
			
			// Deactivate breakpoints
			foreach (Breakpoint b in this.Debugger.Breakpoints) {
				b.IsEnabled = false;
			}
			
			// This is necessary for detach
			foreach(Stepper s in this.Steppers) {
				if (s.CorStepper.IsActive() == 1) {
					s.CorStepper.Deactivate();
				}
			}
			this.Steppers.Clear();
			
			corProcess.Detach();
			
			// modules
			foreach(Module m in this.Modules) {
				m.Dispose();
			}
			
			this.modules.Clear();
			
			// threads
			this.threads.Clear();
			
			OnExited();
		}
		
		public void Continue()
		{
			AsyncContinue();
			WaitForPause();
		}
		
		public void RunTo(string fileName, int line, int column)
		{
			foreach(Module module in this.Modules) {
				SourcecodeSegment segment = SourcecodeSegment.Resolve(module, fileName, line, column);
				if (segment != null) {
					ICorDebugFunctionBreakpoint corBreakpoint = segment.CorFunction.GetILCode().CreateBreakpoint((uint)segment.ILStart);
					corBreakpoint.Activate(1);
					this.tempBreakpoints.Add(corBreakpoint);		
				}
			}
			if (this.IsPaused) {
				AsyncContinue();
			}
		}
		
		internal Thread[] UnsuspendedThreads {
			get {
				List<Thread> unsuspendedThreads = new List<Thread>();
				foreach(Thread t in this.Threads) {
					if (!t.Suspended)
						unsuspendedThreads.Add(t);
				}
				return unsuspendedThreads.ToArray();
			}
		}
		
		/// <summary>
		/// Resume execution and run all threads not marked by the user as susspended.
		/// </summary>
		public void AsyncContinue()
		{
			AsyncContinue(DebuggeeStateAction.Clear, this.UnsuspendedThreads, CorDebugThreadState.THREAD_RUN);
		}
		
		internal CorDebugThreadState NewThreadState = CorDebugThreadState.THREAD_RUN;
		
		/// <param name="threadsToRun"> Null to keep current setting </param>
		/// <param name="newThreadState"> What happens to created threads.  Null to keep current setting </param>
		internal void AsyncContinue(DebuggeeStateAction action, Thread[] threadsToRun, CorDebugThreadState? newThreadState)
		{
			AssertPaused();
			
			if (threadsToRun != null) {
//				corProcess.SetAllThreadsDebugState(CorDebugThreadState.THREAD_SUSPEND, null);
//				Note: There is unreported thread, stopping it prevents the debugee from exiting
//				      It is not corProcess.GetHelperThreadID
//				ICorDebugThread[] ts = new ICorDebugThread[corProcess.EnumerateThreads().GetCount()];
//				corProcess.EnumerateThreads().Next((uint)ts.Length, ts);
				foreach(Thread t in this.Threads) {
					CorDebugThreadState state = Array.IndexOf(threadsToRun, t) == -1 ? CorDebugThreadState.THREAD_SUSPEND : CorDebugThreadState.THREAD_RUN;
					try {
						t.CorThread.SetDebugState(state);
					} catch (COMException e) {
						// The state of the thread is invalid. (Exception from HRESULT: 0x8013132D)
						// It can happen for example when thread has not started yet
						if ((uint)e.ErrorCode == 0x8013132D) {
							// TraceMessage("Can not suspend thread - The state of the thread is invalid.  Thread ID = " + t.CorThread.GetID());
						} else {
							throw;
						}
					}
				}
			}
			
			if (newThreadState != null) {
				this.NewThreadState = newThreadState.Value;
			}
			
			NotifyResumed(action);
			corProcess.Continue(0);
			if (this.Options.Verbose) {
				this.TraceMessage("Continue");
			}
			
			if (action == DebuggeeStateAction.Clear) {
				OnResumed();
			}
		}
		
		/// <summary> Terminates the execution of the process </summary>
		public void Terminate()
		{
			AsyncTerminate();
			// Wait until ExitProcess callback is received
			WaitForExit();
		}
		
		/// <summary> Terminates the execution of the process </summary>
		public void AsyncTerminate()
		{
			// Resume stoped tread
			if (this.IsPaused) {
				// We might get more callbacks so we should maintain consistent sate
				//AsyncContinue(); // Continue the process to get remaining callbacks
			}
			
			// Expose race condition - drain callback queue
			System.Threading.Thread.Sleep(0);
			
			// Stop&terminate - both must be called
			corProcess.Stop(uint.MaxValue);
			corProcess.Terminate(0);
			this.TerminateCommandIssued = true;
			
			// Do not mark the process as exited
			// This is done once ExitProcess callback is received
		}
		
		internal Stepper GetStepper(ICorDebugStepper corStepper)
		{
			foreach(Stepper stepper in this.Steppers) {
				if (stepper.IsCorStepper(corStepper)) {
					return stepper;
				}
			}
			throw new DebuggerException("Stepper is not in collection");
		}
		
		internal void DisableAllSteppers()
		{
			foreach(Thread thread in this.Threads) {
				thread.CurrentStepIn = null;
			}
			foreach(Stepper stepper in this.Steppers) {
				stepper.Ignore = true;
			}
		}
		
		/// <summary>
		/// Waits until the debugger pauses unless it is already paused.
		/// Use PausedReason to find out why it paused.
		/// </summary>
		public void WaitForPause()
		{
			while(this.IsRunning && !this.HasExited) {
				debugger.MTA2STA.WaitForCall();
				debugger.MTA2STA.PerformAllCalls();
			}
			if (this.HasExited) throw new ProcessExitedException();
		}
		
		public void WaitForPause(TimeSpan timeout)
		{
			System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
			watch.Start();
			while(this.IsRunning && !this.HasExited) {
				TimeSpan timeLeft = timeout - watch.Elapsed;
				if (timeLeft <= TimeSpan.FromMilliseconds(10)) break;
				//this.TraceMessage("Time left: " + timeLeft.TotalMilliseconds);
				debugger.MTA2STA.WaitForCall(timeLeft);
				debugger.MTA2STA.PerformAllCalls();
			}
			if (this.HasExited) throw new ProcessExitedException();
		}
		
		/// <summary>
		/// Waits until the precesses exits.
		/// </summary>
		public void WaitForExit()
		{
			while(!this.HasExited) {
				debugger.MTA2STA.WaitForCall();
				debugger.MTA2STA.PerformAllCalls();
			}
		}
		
		#region Break at begining
		
		int lastAssignedModuleOrderOfLoading = 0;
		
		internal void OnModuleLoaded(Module module)
		{
			module.OrderOfLoading = lastAssignedModuleOrderOfLoading++;
			module.AppDomain.InvalidateCompilation();
			
			if (this.BreakInMain) {
				if (module.SymReader == null) return; // No symbols
				
				try {
					// create a BP at entry point
					uint entryPoint = module.SymReader.GetUserEntryPoint();
					if (entryPoint == 0) return; // no EP
					var corBreakpoint = module.CorModule.GetFunctionFromToken(entryPoint).CreateBreakpoint();
					corBreakpoint.Activate(1);
					this.tempBreakpoints.Add(corBreakpoint);
				} catch {
					// the app does not have an entry point - COM exception
				}
				
				this.BreakInMain = false;
			}
			
			if (this.ModuleLoaded != null) {
				this.ModuleLoaded(this, new ModuleEventArgs(module));
			}
		}
		
		internal void OnModuleUnloaded(Module module)
		{
			module.Dispose();
			
			if (this.ModuleUnloaded != null) {
				this.ModuleUnloaded(this, new ModuleEventArgs(module));
			}
		}
		
		#endregion
	}
}
