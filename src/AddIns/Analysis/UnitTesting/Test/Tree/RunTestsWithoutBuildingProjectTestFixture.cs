﻿// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Matthew Ward" email="mrward@users.sourceforge.net"/>
//     <version>$Revision$</version>
// </file>

using System;
using ICSharpCode.UnitTesting;
using NUnit.Framework;
using UnitTesting.Tests.Utils;

namespace UnitTesting.Tests.Tree
{
	[TestFixture]
	public class RunTestsWithoutBuildingProjectTestFixture : RunTestCommandTestFixtureBase
	{
		MockTestFramework testFramework;
		MockBuildProjectBeforeTestRun buildProjectBeforeTestRun;
		
		[SetUp]
		public void Init()
		{
			InitBase();
			
			MockCSharpProject project1 = new MockCSharpProject();
			MockCSharpProject project2 = new MockCSharpProject();
			testFramework = new MockTestFramework();
			testFramework.IsBuildNeededBeforeTestRun = false;
			context.MockRegisteredTestFrameworks.AddTestFrameworkForProject(project1, testFramework);
			context.MockRegisteredTestFrameworks.AddTestFrameworkForProject(project2, testFramework);
			
			buildProjectBeforeTestRun = new MockBuildProjectBeforeTestRun();
			context.MockBuildProjectFactory.AddBuildProjectBeforeTestRun(buildProjectBeforeTestRun);
			
			context.MockUnitTestsPad.AddProject(project1);
			context.MockUnitTestsPad.AddProject(project2);
			
			runTestCommand.Run();
		}
		
		[Test]
		public void TestRunnerIsStarted()
		{
			Assert.IsTrue(runTestCommand.TestRunnersCreated[0].IsStartCalled);
		}
		
		[Test]
		public void ProjectIsNotBuiltBeforeTestRun()
		{
			Assert.IsFalse(buildProjectBeforeTestRun.IsRunMethodCalled);
		}
		
		[Test]
		public void SaveAllFilesCommandIsRun()
		{
			Assert.IsTrue(context.MockSaveAllFilesCommand.IsSaveAllFilesMethodCalled);
		}
		
		[Test]
		public void WhenTestRunCompletedTheSecondProjectIsNotBuilt()
		{
			runTestCommand.CallTestsCompleted();
			Assert.IsFalse(buildProjectBeforeTestRun.IsRunMethodCalled);
		}
		
		[Test]
		public void WhenTestRunCompletedTheSecondTestRunnerIsStarted()
		{
			runTestCommand.CallTestsCompleted();
			Assert.IsTrue(runTestCommand.TestRunnersCreated[1].IsStartCalled);
		}
		
		[Test]
		public void WhenTestRunCompletedAllFilesAreSaved()
		{
			context.MockSaveAllFilesCommand.IsSaveAllFilesMethodCalled = false;
			runTestCommand.CallTestsCompleted();
			Assert.IsTrue(context.MockSaveAllFilesCommand.IsSaveAllFilesMethodCalled);
		}
	}
}
