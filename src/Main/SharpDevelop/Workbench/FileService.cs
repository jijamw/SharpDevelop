﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

using ICSharpCode.AvalonEdit.Utils;
using ICSharpCode.Core;
using ICSharpCode.NRefactory.Editor;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.SharpDevelop.Workbench
{
	sealed class FileService : IFileService
	{
		public FileService()
		{
			SD.ParserService.LoadSolutionProjectsThread.Finished += ParserServiceLoadSolutionProjectsThreadEnded;
		}
		
		void ParserServiceLoadSolutionProjectsThreadEnded(object sender, EventArgs e)
		{
			foreach (IViewContent content in SD.Workbench.ViewContentCollection.ToArray()) {
				DisplayBindingService.AttachSubWindows(content, true);
			}
		}
		
		#region Options
		/// <summary>used for OptionBinding</summary>
		public static FileService Instance {
			get { return (FileService)SD.FileService; }
		}
		
		IRecentOpen recentOpen;
		
		public IRecentOpen RecentOpen {
			get {
				return LazyInitializer.EnsureInitialized(
					ref recentOpen, () => new RecentOpen(PropertyService.NestedProperties("RecentOpen")));
			}
		}
		
		public bool DeleteToRecycleBin {
			get {
				return PropertyService.Get("SharpDevelop.DeleteToRecycleBin", true);
			}
			set {
				PropertyService.Set("SharpDevelop.DeleteToRecycleBin", value);
			}
		}
		
		public bool SaveUsingTemporaryFile {
			get {
				return PropertyService.Get("SharpDevelop.SaveUsingTemporaryFile", true);
			}
			set {
				PropertyService.Set("SharpDevelop.SaveUsingTemporaryFile", value);
			}
		}
		#endregion
		
		#region DefaultFileEncoding
		public int DefaultFileEncodingCodePage {
			get { return PropertyService.Get("SharpDevelop.DefaultFileEncoding", 65001); }
			set { PropertyService.Set("SharpDevelop.DefaultFileEncoding", value); }
		}
		
		public Encoding DefaultFileEncoding {
			get {
				return Encoding.GetEncoding(DefaultFileEncodingCodePage);
			}
		}
		
		readonly EncodingInfo[] allEncodings = Encoding.GetEncodings().OrderBy(e => e.DisplayName).ToArray();
		
		public IReadOnlyList<EncodingInfo> AllEncodings {
			get { return allEncodings; }
		}
		
		public EncodingInfo DefaultFileEncodingInfo {
			get {
				int cp = DefaultFileEncodingCodePage;
				return allEncodings.Single(e => e.CodePage == cp);
			}
			set {
				DefaultFileEncodingCodePage = value.CodePage;
			}
		}
		#endregion
		
		#region GetFileContent
		public ITextSource GetFileContent(FileName fileName)
		{
			return GetFileContentForOpenFile(fileName) ?? GetFileContentFromDisk(fileName, CancellationToken.None);
		}
		
		public ITextSource GetFileContent(string fileName)
		{
			return GetFileContent(FileName.Create(fileName));
		}
		
		public ITextSource GetFileContentForOpenFile(FileName fileName)
		{
			return SD.MainThread.InvokeIfRequired(
				delegate {
					OpenedFile file = this.GetOpenedFile(fileName);
					if (file != null) {
						IFileDocumentProvider p = file.CurrentView as IFileDocumentProvider;
						if (p != null) {
							IDocument document = p.GetDocumentForFile(file);
							if (document != null) {
								return document.CreateSnapshot();
							}
						}
						
						using (Stream s = file.OpenRead()) {
							// load file
							return new StringTextSource(FileReader.ReadFileContent(s, DefaultFileEncoding));
						}
					}
					return null;
				});
		}
		
		public ITextSource GetFileContentFromDisk(FileName fileName, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string text = FileReader.ReadFileContent(fileName, DefaultFileEncoding);
			DateTime lastWriteTime = File.GetLastWriteTimeUtc(fileName);
			return new StringTextSource(text, new OnDiskTextSourceVersion(lastWriteTime));
		}
		#endregion
		
		#region BrowseForFolder
		public string BrowseForFolder(string description, string selectedPath)
		{
			using (FolderBrowserDialog dialog = new FolderBrowserDialog()) {
				dialog.Description = StringParser.Parse(description);
				if (selectedPath != null && selectedPath.Length > 0 && Directory.Exists(selectedPath)) {
					dialog.RootFolder = Environment.SpecialFolder.MyComputer;
					dialog.SelectedPath = selectedPath;
				}
				if (dialog.ShowDialog() == DialogResult.OK) {
					return dialog.SelectedPath;
				} else {
					return null;
				}
			}
		}
		#endregion
		
		#region OpenedFile
		Dictionary<FileName, OpenedFile> openedFileDict = new Dictionary<FileName, OpenedFile>();
		
		/// <inheritdoc/>
		public IReadOnlyList<OpenedFile> OpenedFiles {
			get {
				SD.MainThread.VerifyAccess();
				return openedFileDict.Values.ToArray();
			}
		}
		
		/// <inheritdoc/>
		public OpenedFile GetOpenedFile(string fileName)
		{
			return GetOpenedFile(FileName.Create(fileName));
		}
		
		/// <inheritdoc/>
		public OpenedFile GetOpenedFile(FileName fileName)
		{
			if (fileName == null)
				throw new ArgumentNullException("fileName");
			
			SD.MainThread.VerifyAccess();
			
			OpenedFile file;
			openedFileDict.TryGetValue(fileName, out file);
			return file;
		}
		
		/// <inheritdoc/>
		public OpenedFile GetOrCreateOpenedFile(string fileName)
		{
			return GetOrCreateOpenedFile(FileName.Create(fileName));
		}
		
		/// <inheritdoc/>
		public OpenedFile GetOrCreateOpenedFile(FileName fileName)
		{
			if (fileName == null)
				throw new ArgumentNullException("fileName");
			
			OpenedFile file;
			if (!openedFileDict.TryGetValue(fileName, out file)) {
				openedFileDict[fileName] = file = new FileServiceOpenedFile(this, fileName);
			}
			return file;
		}
		
		/// <inheritdoc/>
		public OpenedFile CreateUntitledOpenedFile(string defaultName, byte[] content)
		{
			if (defaultName == null)
				throw new ArgumentNullException("defaultName");
			
			OpenedFile file = new FileServiceOpenedFile(this, content);
			file.FileName = new FileName(file.GetHashCode() + "/" + defaultName);
			openedFileDict[file.FileName] = file;
			return file;
		}
		
		/// <summary>Called by OpenedFile.set_FileName to update the dictionary.</summary>
		internal void OpenedFileFileNameChange(OpenedFile file, FileName oldName, FileName newName)
		{
			if (oldName == null) return; // File just created with NewFile where name is being initialized.
			
			LoggingService.Debug("OpenedFileFileNameChange: " + oldName + " => " + newName);
			
			if (openedFileDict[oldName] != file)
				throw new ArgumentException("file must be registered as oldName");
			if (openedFileDict.ContainsKey(newName)) {
				OpenedFile oldFile = openedFileDict[newName];
				if (oldFile.CurrentView != null) {
					oldFile.CurrentView.WorkbenchWindow.CloseWindow(true);
				} else {
					throw new ArgumentException("there already is a file with the newName");
				}
			}
			openedFileDict.Remove(oldName);
			openedFileDict[newName] = file;
		}
		
		/// <summary>Called by OpenedFile.UnregisterView to update the dictionary.</summary>
		internal void OpenedFileClosed(OpenedFile file)
		{
			OpenedFile existing;
			if (openedFileDict.TryGetValue(file.FileName, out existing) && existing != file)
				throw new ArgumentException("file must be registered");
			
			openedFileDict.Remove(file.FileName);
			LoggingService.Debug("OpenedFileClosed: " + file.FileName);
		}
		#endregion
		
		#region CheckFileName
		/// <inheritdoc/>
		public bool CheckFileName(string path)
		{
			if (FileUtility.IsValidPath(path))
				return true;
			MessageService.ShowMessage(StringParser.Parse("${res:ICSharpCode.SharpDevelop.Commands.SaveFile.InvalidFileNameError}", new StringTagPair("FileName", path)));
			return false;
		}
		
		/// <inheritdoc/>
		public bool CheckDirectoryEntryName(string name)
		{
			if (FileUtility.IsValidDirectoryEntryName(name))
				return true;
			MessageService.ShowMessage(StringParser.Parse("${res:ICSharpCode.SharpDevelop.Commands.SaveFile.InvalidFileNameError}", new StringTagPair("FileName", name)));
			return false;
		}
		#endregion
		
		#region OpenFile (ViewContent)
		/// <inheritdoc/>
		public bool IsOpen(FileName fileName)
		{
			return GetOpenFile(fileName) != null;
		}
		
		/// <inheritdoc/>
		public IViewContent OpenFile(FileName fileName)
		{
			return OpenFile(fileName, true);
		}
		
		/// <inheritdoc/>
		public IViewContent OpenFile(FileName fileName, bool switchToOpenedView)
		{
			LoggingService.Info("Open file " + fileName);
			
			IViewContent viewContent = GetOpenFile(fileName);
			if (viewContent != null) {
				if (switchToOpenedView) {
					viewContent.WorkbenchWindow.SelectWindow();
				}
				return viewContent;
			}
			
			IDisplayBinding binding = DisplayBindingService.GetBindingPerFileName(fileName);
			
			if (binding == null) {
				binding = new ErrorFallbackBinding("Could not find any display binding for " + Path.GetFileName(fileName));
			}
			if (FileUtility.ObservedLoad(new NamedFileOperationDelegate(new LoadFileWrapper(binding, switchToOpenedView).Invoke), fileName) == FileOperationResult.OK) {
				RecentOpen.AddRecentFile(fileName);
			}
			return GetOpenFile(fileName);
		}
		
		/// <inheritdoc/>
		public IViewContent OpenFileWith(FileName fileName, IDisplayBinding displayBinding, bool switchToOpenedView)
		{
			if (displayBinding == null)
				throw new ArgumentNullException("displayBinding");
			if (FileUtility.ObservedLoad(new NamedFileOperationDelegate(new LoadFileWrapper(displayBinding, switchToOpenedView).Invoke), fileName) == FileOperationResult.OK) {
				RecentOpen.AddRecentFile(fileName);
			}
			return GetOpenFile(fileName);
		}
		
		sealed class LoadFileWrapper
		{
			readonly IDisplayBinding binding;
			readonly bool switchToOpenedView;
			
			public LoadFileWrapper(IDisplayBinding binding, bool switchToOpenedView)
			{
				this.binding = binding;
				this.switchToOpenedView = switchToOpenedView;
			}
			
			public void Invoke(string fileName)
			{
				OpenedFile file = SD.FileService.GetOrCreateOpenedFile(FileName.Create(fileName));
				try {
					IViewContent newContent = binding.CreateContentForFile(file);
					if (newContent != null) {
						DisplayBindingService.AttachSubWindows(newContent, false);
						WorkbenchSingleton.Workbench.ShowView(newContent, switchToOpenedView);
					}
				} finally {
					file.CloseIfAllViewsClosed();
				}
			}
		}
		
		/// <inheritdoc/>
		public IViewContent NewFile(string defaultName, string content)
		{
			return NewFile(defaultName, DefaultFileEncoding.GetBytesWithPreamble(content));
		}
		
		/// <inheritdoc/>
		public IViewContent NewFile(string defaultName, byte[] content)
		{
			if (defaultName == null)
				throw new ArgumentNullException("defaultName");
			if (content == null)
				throw new ArgumentNullException("content");
			
			IDisplayBinding binding = DisplayBindingService.GetBindingPerFileName(defaultName);
			
			if (binding == null) {
				binding = new ErrorFallbackBinding("Can't create display binding for file " + defaultName);
			}
			OpenedFile file = CreateUntitledOpenedFile(defaultName, content);
			
			IViewContent newContent = binding.CreateContentForFile(file);
			if (newContent == null) {
				LoggingService.Warn("Created view content was null - DefaultName:" + defaultName);
				file.CloseIfAllViewsClosed();
				return null;
			}
			
			DisplayBindingService.AttachSubWindows(newContent, false);
			
			WorkbenchSingleton.Workbench.ShowView(newContent);
			return newContent;
		}
		
		/// <inheritdoc/>
		public IReadOnlyList<FileName> OpenPrimaryFiles {
			get {
				List<FileName> fileNames = new List<FileName>();
				foreach (IViewContent content in WorkbenchSingleton.Workbench.ViewContentCollection) {
					FileName contentName = content.PrimaryFileName;
					if (contentName != null && !fileNames.Contains(contentName))
						fileNames.Add(contentName);
				}
				return fileNames;
			}
		}
		
		/// <inheritdoc/>
		public IViewContent GetOpenFile(FileName fileName)
		{
			if (fileName != null) {
				foreach (IViewContent content in WorkbenchSingleton.Workbench.ViewContentCollection) {
					string contentName = content.PrimaryFileName;
					if (contentName != null) {
						if (FileUtility.IsEqualFileName(fileName, contentName))
							return content;
					}
				}
			}
			return null;
		}
		
		sealed class ErrorFallbackBinding : IDisplayBinding
		{
			string errorMessage;
			
			public ErrorFallbackBinding(string errorMessage)
			{
				this.errorMessage = errorMessage;
			}
			
			public bool CanCreateContentForFile(string fileName)
			{
				return true;
			}
			
			public IViewContent CreateContentForFile(OpenedFile file)
			{
				return new SimpleViewContent(errorMessage) { TitleName = Path.GetFileName(file.FileName) };
			}
			
			public bool IsPreferredBindingForFile(string fileName)
			{
				return false;
			}
			
			public double AutoDetectFileContent(string fileName, Stream fileContent, string detectedMimeType)
			{
				return double.NegativeInfinity;
			}
		}
		
		/// <inheritdoc/>
		public IViewContent JumpToFilePosition(FileName fileName, int line, int column)
		{
			LoggingService.InfoFormatted("FileService\n\tJumping to File Position:  [{0} : {1}x{2}]", fileName, line, column);
			
			if (fileName == null) {
				return null;
			}
			
			NavigationService.SuspendLogging();
			bool loggingResumed = false;
			
			try {
				IViewContent content = OpenFile(fileName);
				if (content is IPositionable) {
					// TODO: enable jumping to a particular view
					content.WorkbenchWindow.ActiveViewContent = content;
					NavigationService.ResumeLogging();
					loggingResumed = true;
					((IPositionable)content).JumpTo(Math.Max(1, line), Math.Max(1, column));
				} else {
					NavigationService.ResumeLogging();
					loggingResumed = true;
					NavigationService.Log(content);
				}
				
				return content;
				
			} finally {
				LoggingService.InfoFormatted("FileService\n\tJumped to File Position:  [{0} : {1}x{2}]", fileName, line, column);
				
				if (!loggingResumed) {
					NavigationService.ResumeLogging();
				}
			}
		}
		#endregion
		
		#region Remove/Rename/Copy
		/// <summary>
		/// Removes a file, raising the appropriate events. This method may show message boxes.
		/// </summary>
		public void RemoveFile(string fileName, bool isDirectory)
		{
			FileCancelEventArgs eargs = new FileCancelEventArgs(fileName, isDirectory);
			OnFileRemoving(eargs);
			if (eargs.Cancel)
				return;
			if (!eargs.OperationAlreadyDone) {
				if (isDirectory) {
					try {
						if (Directory.Exists(fileName)) {
							if (SD.FileService.DeleteToRecycleBin)
								NativeMethods.DeleteToRecycleBin(fileName);
							else
								Directory.Delete(fileName, true);
						}
					} catch (Exception e) {
						MessageService.ShowHandledException(e, "Can't remove directory " + fileName);
					}
				} else {
					try {
						if (File.Exists(fileName)) {
							if (SD.FileService.DeleteToRecycleBin)
								NativeMethods.DeleteToRecycleBin(fileName);
							else
								File.Delete(fileName);
						}
					} catch (Exception e) {
						MessageService.ShowHandledException(e, "Can't remove file " + fileName);
					}
				}
			}
			OnFileRemoved(new FileEventArgs(fileName, isDirectory));
		}
		
		/// <summary>
		/// Renames or moves a file, raising the appropriate events. This method may show message boxes.
		/// </summary>
		public bool RenameFile(string oldName, string newName, bool isDirectory)
		{
			if (FileUtility.IsEqualFileName(oldName, newName))
				return false;
			FileChangeWatcher.DisableAllChangeWatchers();
			try {
				FileRenamingEventArgs eargs = new FileRenamingEventArgs(oldName, newName, isDirectory);
				OnFileRenaming(eargs);
				if (eargs.Cancel)
					return false;
				if (!eargs.OperationAlreadyDone) {
					try {
						if (isDirectory && Directory.Exists(oldName)) {
							
							if (Directory.Exists(newName)) {
								MessageService.ShowMessage(StringParser.Parse("${res:Gui.ProjectBrowser.FileInUseError}"));
								return false;
							}
							Directory.Move(oldName, newName);
							
						} else if (File.Exists(oldName)) {
							if (File.Exists(newName)) {
								MessageService.ShowMessage(StringParser.Parse("${res:Gui.ProjectBrowser.FileInUseError}"));
								return false;
							}
							File.Move(oldName, newName);
						}
					} catch (Exception e) {
						if (isDirectory) {
							MessageService.ShowHandledException(e, "Can't rename directory " + oldName);
						} else {
							MessageService.ShowHandledException(e, "Can't rename file " + oldName);
						}
						return false;
					}
				}
				OnFileRenamed(new FileRenameEventArgs(oldName, newName, isDirectory));
				return true;
			} finally {
				FileChangeWatcher.EnableAllChangeWatchers();
			}
		}
		
		/// <summary>
		/// Copies a file, raising the appropriate events. This method may show message boxes.
		/// </summary>
		public bool CopyFile(string oldName, string newName, bool isDirectory, bool overwrite)
		{
			if (FileUtility.IsEqualFileName(oldName, newName))
				return false;
			FileRenamingEventArgs eargs = new FileRenamingEventArgs(oldName, newName, isDirectory);
			OnFileCopying(eargs);
			if (eargs.Cancel)
				return false;
			if (!eargs.OperationAlreadyDone) {
				try {
					if (isDirectory && Directory.Exists(oldName)) {
						
						if (!overwrite && Directory.Exists(newName)) {
							MessageService.ShowMessage(StringParser.Parse("${res:Gui.ProjectBrowser.FileInUseError}"));
							return false;
						}
						FileUtility.DeepCopy(oldName, newName, overwrite);
						
					} else if (File.Exists(oldName)) {
						if (!overwrite && File.Exists(newName)) {
							MessageService.ShowMessage(StringParser.Parse("${res:Gui.ProjectBrowser.FileInUseError}"));
							return false;
						}
						File.Copy(oldName, newName, overwrite);
					}
				} catch (Exception e) {
					if (isDirectory) {
						MessageService.ShowHandledException(e, "Can't copy directory " + oldName);
					} else {
						MessageService.ShowHandledException(e, "Can't copy file " + oldName);
					}
					return false;
				}
			}
			OnFileCopied(new FileRenameEventArgs(oldName, newName, isDirectory));
			return true;
		}
		
		void OnFileRemoved(FileEventArgs e)
		{
			if (FileRemoved != null) {
				FileRemoved(this, e);
			}
		}
		
		void OnFileRemoving(FileCancelEventArgs e)
		{
			if (FileRemoving != null) {
				FileRemoving(this, e);
			}
		}
		
		void OnFileRenamed(FileRenameEventArgs e)
		{
			if (FileRenamed != null) {
				FileRenamed(this, e);
			}
		}
		
		void OnFileRenaming(FileRenamingEventArgs e)
		{
			if (FileRenaming != null) {
				FileRenaming(this, e);
			}
		}
		
		void OnFileCopied(FileRenameEventArgs e)
		{
			if (FileCopied != null) {
				FileCopied(this, e);
			}
		}
		
		void OnFileCopying(FileRenamingEventArgs e)
		{
			if (FileCopying != null) {
				FileCopying(this, e);
			}
		}
		
		public event EventHandler<FileRenamingEventArgs> FileRenaming;
		public event EventHandler<FileRenameEventArgs> FileRenamed;
		
		public event EventHandler<FileRenamingEventArgs> FileCopying;
		public event EventHandler<FileRenameEventArgs> FileCopied;
		
		public event EventHandler<FileCancelEventArgs> FileRemoving;
		public event EventHandler<FileEventArgs> FileRemoved;
		#endregion
		
		#region FileCreated/Replaced
		/// <summary>
		/// Fires the event handlers for a file being created.
		/// </summary>
		/// <param name="fileName">The name of the file being created. This should be a fully qualified path.</param>
		/// <param name="isDirectory">Set to true if this is a directory</param>
		/// <returns>True if the operation can proceed, false if an event handler cancelled the operation.</returns>
		public bool FireFileReplacing(string fileName, bool isDirectory)
		{
			FileCancelEventArgs e = new FileCancelEventArgs(fileName, isDirectory);
			if (FileReplacing != null) {
				FileReplacing(this, e);
			}
			return !e.Cancel;
		}
		
		/// <summary>
		/// Fires the event handlers for a file being replaced.
		/// </summary>
		/// <param name="fileName">The name of the file being created. This should be a fully qualified path.</param>
		/// <param name="isDirectory">Set to true if this is a directory</param>
		public void FireFileReplaced(string fileName, bool isDirectory)
		{
			if (FileReplaced != null) {
				FileReplaced(this, new FileEventArgs(fileName, isDirectory));
			}
		}
		
		/// <summary>
		/// Fires the event handlers for a file being created.
		/// </summary>
		/// <param name="fileName">The name of the file being created. This should be a fully qualified path.</param>
		/// <param name="isDirectory">Set to true if this is a directory</param>
		public void FireFileCreated(string fileName, bool isDirectory)
		{
			if (FileCreated != null) {
				FileCreated(this, new FileEventArgs(fileName, isDirectory));
			}
		}
		
		public event EventHandler<FileEventArgs> FileCreated;
		public event EventHandler<FileCancelEventArgs> FileReplacing;
		public event EventHandler<FileEventArgs> FileReplaced;
		#endregion
	}
}
