﻿// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Matthew Ward" email="mrward@users.sourceforge.net"/>
//     <version>$Revision$</version>
// </file>

using System;
using ICSharpCode.SharpDevelop.Util;

namespace ICSharpCode.UnitTesting
{
	public interface IUnitTestProcessRunner
	{
		bool LogStandardOutputAndError { get; set; }
		string WorkingDirectory { get; set; }
		
		void Start(string command, string arguments);
		void Kill();
		
		event LineReceivedEventHandler OutputLineReceived;
		event LineReceivedEventHandler ErrorLineReceived;
		event EventHandler ProcessExited;
	}
}
