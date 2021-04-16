﻿/*
* Apache License, Version 2.0
* Copyright 2019-2021 NVIDIA Corporation
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
*     http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using FluentAssertions;
using Moq;
using Nvidia.Clara.DicomAdapter.API;
using Nvidia.Clara.DicomAdapter.Test.Shared;
using Nvidia.Clara.Platform;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using xRetry;
using Xunit;

namespace Nvidia.Clara.DicomAdapter.Test.Integration
{
    [Collection("DICOM Adapter")]
    public class StoreScpTest : IClassFixture<ScpTestFileSetsFixture>, IAsyncDisposable
    {
        private readonly DicomAdapterFixture _dicomAdapterFixture;
        private readonly TestFileSetsFixture _testFileSetsFixture;

        public StoreScpTest(DicomAdapterFixture dicomAdapterFixture, ScpTestFileSetsFixture testFileSetsFixture)
        {
            _dicomAdapterFixture = dicomAdapterFixture;
            _testFileSetsFixture = testFileSetsFixture;

            _dicomAdapterFixture.ResetMocks();
        }

        public async ValueTask DisposeAsync()
        {
            await _dicomAdapterFixture.DisposeAsync();
        }

        [RetryFact(DisplayName = "C-STORE SCP Rejects unknown source")]
        public void ScpShouldRejectUnknownSource()
        {
            var testCase = "1-scp-explicitVrLittleEndian";
            int exitCode = 0;
            var output = DcmtkLauncher.StoreScu(
                _testFileSetsFixture.FileSetPaths[testCase].First().FileDirectoryPath,
                 "-xb",
                 $"-v -aet UNKNOWN -aec {DicomAdapterFixture.AET_CLARA1}",
                 out exitCode);

            Assert.Equal(1, exitCode);
            output.Where(p => p == "F: Reason: Calling AE Title Not Recognized").Should().HaveCount(1);
        }

        [RetryFact(DisplayName = "C-STORE SCP Rejects unknown called AE Title")]
        public void ScpShouldRejectUnknownCalledAeTitle()
        {
            var testCase = "1-scp-explicitVrLittleEndian";
            int exitCode = 0;
            var output = DcmtkLauncher.StoreScu(
                _testFileSetsFixture.FileSetPaths[testCase].First().FileDirectoryPath, "-xb", $"-v -aet PACS1 -aec UNKNOWN",
                 out exitCode);

            Assert.Equal(1, exitCode);
            output.Where(p => p == "F: Reason: Called AE Title Not Recognized").Should().HaveCount(1);
        }

        [RetryFact(10, DisplayName = "C-STORE SCP shall be able to accept multiple associations over multiple AE Titles")]
        public void ScpShallAcceptMultipleAssociationsOverMultipleAetitles()
        {
            var testCase = "2-2-patients-2-studies";
            // Clara1 with 4 studies launched
            // Clara2 with 2 patients * 2 pipelines launched
            var jobCreatedEvent = new CountdownEvent(8);
            var jobs = new List<Job>();
            var instanceStoredCounter = new MockedStoredInstanceObserver();
            _dicomAdapterFixture.GetIInstanceStoredNotificationService().Subscribe(instanceStoredCounter);

            _dicomAdapterFixture.Jobs.Setup(p => p.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JobPriority>(), It.IsAny<IDictionary<string, string>>()))
                .Callback((string pipelineId, string jobName, JobPriority jobPriority, IDictionary<string, string> metadata) =>
                {
                    jobCreatedEvent.Signal();
                    Console.WriteLine(">>>>> Job.Create {0} - {1} - {2}", pipelineId, jobName, jobPriority);
                })
                .Returns((string pipelineId, string jobName, JobPriority jobPriority, IDictionary<string, string> metadata) => Task.FromResult(new Job
                {
                    JobId = Guid.NewGuid().ToString(),
                    PayloadId = Guid.NewGuid().ToString()
                }));

            var outputs = new List<StringBuilder>();
            var processes = new List<Tuple<Process, ManualResetEvent>>();
            var paths = new List<string>();

            var rootPath = Path.Combine(TestFileSetsFixture.ApplicationEntryDirectory, testCase);

            foreach (var patient in new[] { "P0", "P1" })
            {
                foreach (var study in new[] { "S0", "S1" })
                {
                    var path = Path.Combine(rootPath, patient, study);
                    paths.Add(path);
                    paths.Add(path);

                    var output1 = new StringBuilder();
                    var proc1 = DcmtkLauncher.StoreScuNoWait(path, $"-xb", $"-v -aet PACS1 -aec {DicomAdapterFixture.AET_CLARA1}", output1);
                    outputs.Add(output1);
                    processes.Add(proc1);

                    var output2 = new StringBuilder();
                    var proc2 = DcmtkLauncher.StoreScuNoWait(path, $"-xb", $"-v -aet PACS1 -aec {DicomAdapterFixture.AET_CLARA2}", output2);
                    outputs.Add(output2);
                    processes.Add(proc2);
                }
            }

            int totalInstanceSent = 0;
            int threadSleep = 2000;
            for (var i = 0; i < processes.Count; i++)
            {
                processes[i].Item1.WaitForExit();
                processes[i].Item2.WaitOne(3000);
                Console.WriteLine(">>>>> Association #{0}", i);
                // if (processes[i].ExitCode != 0)
                //     Console.WriteLine(">>>>> {0}", outputs[i].ToString());
                Assert.Equal(0, processes[i].Item1.ExitCode);
                // Console.WriteLine(outputs[i].ToString());

                // make sure we are sending correct number of files
                var instanceSent = Directory.GetFiles(paths[i]).Count();
                totalInstanceSent += instanceSent;
                Thread.Sleep(threadSleep);
                outputs[i].ToString().Split(Environment.NewLine)
                    .Where(p => p.Contains("I: Received Store Response (Success)"))
                    .Should()
                    .HaveCount(instanceSent, outputs[i].ToString());
                threadSleep -= 125;
            }
            Assert.True(jobCreatedEvent.Wait(TimeSpan.FromSeconds(60)));
            Assert.Equal(totalInstanceSent, instanceStoredCounter.InstanceCount);
        }

        [RetryFact(DisplayName = "C-STORE SCP shall be able to compose a single job from multiple associations")]
        public void ScpShallComposeSingleJobFromMultipleAssociations()
        {
            var testCase = "3-single-study-multi-series";
            var jobs = new List<Job>();
            var jobCreatedEvent = new CountdownEvent(1);
            var instanceStoredCounter = new MockedStoredInstanceObserver();
            _dicomAdapterFixture.GetIInstanceStoredNotificationService().Subscribe(instanceStoredCounter);

            _dicomAdapterFixture.Jobs.Setup(p => p.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JobPriority>(), It.IsAny<IDictionary<string, string>>()))
                .Callback((string pipelineId, string jobName, JobPriority jobPriority, IDictionary<string, string> metadata) =>
                {
                    Console.WriteLine(">>>>> Job.Create {0} - {1} - {2}", pipelineId, jobName, jobPriority);
                    jobCreatedEvent.Signal();
                })
                .Returns((string pipelineId, string jobName, JobPriority jobPriority, IDictionary<string, string> metadata) => Task.FromResult(new Job
                {
                    JobId = Guid.NewGuid().ToString(),
                    PayloadId = Guid.NewGuid().ToString()
                }));

            var outputs = new List<StringBuilder>();
            var processes = new List<Tuple<Process, ManualResetEvent>>();
            var paths = new List<string>();

            var rootPath = Path.Combine(TestFileSetsFixture.ApplicationEntryDirectory, testCase);

            for (var i = 0; i < 5; i++)
            {
                var path = Path.Combine(rootPath, i.ToString());
                paths.Add(path);
                var output1 = new StringBuilder();
                var proc1 = DcmtkLauncher.StoreScuNoWait(path, $"-xb", $"-v -aet PACS1 -aec {DicomAdapterFixture.AET_CLARA1}", output1);
                outputs.Add(output1);
                processes.Add(proc1);
            }

            var totalInstanceSent = 0;
            for (var i = 0; i < processes.Count; i++)
            {
                processes[i].Item1.WaitForExit();
                processes[i].Item2.WaitOne(3000);
                Console.WriteLine(">>>>> Association #{0}", i);
                if (processes[i].Item1.ExitCode != 0)
                    Console.WriteLine(">>>>> {0}", outputs[i].ToString());
                Assert.Equal(0, processes[i].Item1.ExitCode);
                // Console.WriteLine(outputs[i].ToString());

                // make sure we are sending correct number of files
                var instanceSent = Directory.GetFiles(paths[i]).Count();
                totalInstanceSent += instanceSent;
                Thread.Sleep(750);
                var receivedStoreCount = outputs[i].ToString().Split(Environment.NewLine)
                    .Count(p => p.Contains("I: Received Store Response (Success)"));

                var sendingStoreCount = outputs[i].ToString().Split(Environment.NewLine)
                    .Count(p => p.Contains("I: Sending Store Request"));

                // Since receivedStoreCount can sometime be off by 1, we'll relax the check here
                Assert.InRange(receivedStoreCount + sendingStoreCount, instanceSent * 2 - 1, instanceSent * 2);
            }
            Assert.True(jobCreatedEvent.Wait(TimeSpan.FromSeconds(30)));
            Assert.Equal(totalInstanceSent, instanceStoredCounter.InstanceCount);
        }
    }

    internal class MockedStoredInstanceObserver : IObserver<InstanceStorageInfo>
    {
        private int instanceCount;

        public int InstanceCount { get => instanceCount; }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(InstanceStorageInfo value)
        {
            Interlocked.Increment(ref instanceCount);
        }
    }
}