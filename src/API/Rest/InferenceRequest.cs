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

using Ardalis.GuardClauses;
using Newtonsoft.Json;
using Nvidia.Clara.DicomAdapter.Common;
using Nvidia.Clara.Platform;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nvidia.Clara.DicomAdapter.API.Rest
{
    /// <summary>
    /// Status of an inference request.
    /// </summary>
    public enum InferenceRequestStatus
    {
        Unknown,
        Success,
        Fail
    }

    /// <summary>
    /// State of a inference request.
    /// </summary>
    public enum InferenceRequestState
    {
        /// <summary>
        /// Indicates that an inference request is currently queued for data retrieval.
        /// </summary>
        Queued,

        /// <summary>
        /// The inference request is being processing by DICOM Adapter.
        /// </summary>
        InProcess,

        /// <summary>
        /// Indicates DICOM Adapter has submitted a new pipeline job with the Clara Platform.
        /// </summary>
        Completed,
    }

    /// <summary>
    /// Structure that represents an inference request based on ACR's Platform-Model Communication for AI.
    /// </summary>
    /// <example>
    /// <code>
    /// {
    ///     "transactionID": "ABCDEF123456",
    ///     "priority": "255",
    ///     "inputMetadata": { ... },
    ///     "inputResources": [ ... ],
    ///     "outputResources": [ ... ]
    /// }
    /// </code>
    /// </example>
    /// <remarks>
    /// Refer to [ACR DSI Model API](https://www.acrdsi.org/-/media/DSI/Files/ACR-DSI-Model-API.pdf)
    /// for more information.
    /// <para><c>transactionID></c> is required.</para>
    /// <para><c>inputMetadata></c> is required.</para>
    /// <para><c>inputResources></c> is required.</para>
    /// </remarks>
    public class InferenceRequest
    {
        /// <summary>
        /// Gets or set the transaction ID of a request.
        /// </summary>
        [JsonProperty(PropertyName = "transactionID")]
        public string TransactionId { get; set; }

        /// <summary>
        /// Gets or sets the priority of a request.
        /// </summary>
        /// <remarks>
        /// <para>Default value is <c>128</c> which maps to <c>JOB_PRIORITY_NORMAL</c>.</para>
        /// <para>Any value lower than <c>128</c> is map to <c>JOB_PRIORITY_LOWER</c>.</para>
        /// <para>Any value between <c>129-254</c> (inclusive) is set to <c>JOB_PRIORITY_HIGHER</c>.</para>
        /// <para>Value of <c>255</c> maps to <c>JOB_PRIORITY_IMMEDIATE</c>.</para>
        /// </remarks>
        [JsonProperty(PropertyName = "priority")]
        public byte Priority { get; set; } = 128;

        /// <summary>
        /// Gets or sets the details of the data associated with the inference request.
        /// </summary>
        [JsonProperty(PropertyName = "inputMetadata")]
        public InferenceRequestMetadata InputMetadata { get; set; }

        /// <summary>
        /// Gets or set a list of data sources to query/retrieve data from.
        /// When multiple data sources are specified, the system will query based on
        /// the order the list was received.
        /// </summary>
        [JsonProperty(PropertyName = "inputResources")]
        public IList<RequestInputDataResource> InputResources { get; set; }

        /// <summary>
        /// Gets or set a list of data sources to export results to.
        /// In order to export via DICOMweb, the Clara Pipeline must include
        /// and use Register Results Operator and register the results with agent
        /// name "DICOMweb" or the values configured in dicom>scu>export>agent field.
        /// </summary>
        [JsonProperty(PropertyName = "outputResources")]
        public IList<RequestOutputDataResource> OutputResources { get; set; }

        #region Internal Use Only
        public Guid InferenceRequestId { get; set; } = Guid.NewGuid();

        public string JobId { get; set; }

        public string PayloadId { get; set; }

        public InferenceRequestState State { get; set; } = InferenceRequestState.Queued;

        public InferenceRequestStatus Status { get; set; } = InferenceRequestStatus.Unknown;

        public string StoragePath { get; set; }

        public int TryCount { get; set; } = 0;

        [JsonIgnore]
        public InputConnectionDetails Algorithm
        {
            get
            {
                return InputResources.FirstOrDefault(predicate => predicate.Interface == InputInterfaceType.Algorithm)?.ConnectionDetails;
            }
        }

        [JsonIgnore]
        public JobPriority ClaraJobPriority
        {
            get
            {
                switch (Priority)
                {
                    case byte n when (n < 128):
                        return JobPriority.Lower;

                    case byte n when (n == 128):
                        return JobPriority.Normal;

                    case byte n when (n == 255):
                        return JobPriority.Immediate;

                    default:
                        return JobPriority.Higher;
                }
            }
        }

        [JsonIgnore]
        public string JobName
        {
            get
            {
                return $"{Algorithm.Name}-{DateTime.UtcNow.ToString("dd-HHmmss")}".FixJobName();
            }
        }
        #endregion

        public InferenceRequest()
        {
            InputResources = new List<RequestInputDataResource>();
            OutputResources = new List<RequestOutputDataResource>();
        }

        /// <summary>
        /// Configures temporary storage location used to store retrieved data.
        /// </summary>
        /// <param name="temporaryStorageRoot">Root path to the temporary storage location.</param>
        public void ConfigureTemporaryStorageLocation(string storagePath)
        {
            Guard.Against.NullOrWhiteSpace(storagePath, nameof(storagePath));
            if (!string.IsNullOrWhiteSpace(StoragePath))
            {
                throw new InferenceRequestException("StoragePath already configured.");
            }

            StoragePath = storagePath;
        }

        public bool IsValid(out string details)
        {
            var errors = new List<string>();

            if (InputResources.IsNullOrEmpty() ||
                InputResources.Count(predicate => predicate.Interface != InputInterfaceType.Algorithm) == 0)
            {
                errors.Add("No 'intputResources' specified.");
            }

            if (Algorithm is null)
            {
                errors.Add("No algorithm defined or more than one algorithms defined in 'inputResources'.  'inputResources' must include one algorithm/pipeline for the inference request.");
            }

            if (InputMetadata?.Details is null)
            {
                errors.Add("Request has no `inputMetadata` defined.");
            }
            else
            {
                switch (InputMetadata.Details.Type)
                {
                    case InferenceRequestType.DicomUid:
                        if (InputMetadata.Details.Studies.IsNullOrEmpty())
                        {
                            errors.Add("Request type is set to `DICOM_UID` but no `studies` defined.");
                        }
                        break;

                    case InferenceRequestType.DicomPatientId:
                        if (string.IsNullOrWhiteSpace(InputMetadata.Details.PatientId))
                        {
                            errors.Add("Request type is set to `DICOM_PATIENT_ID` but `PatientID` is not defined.");
                        }
                        break;

                    case InferenceRequestType.AccessionNumber:
                        if (InputMetadata.Details.AccessionNumber.IsNullOrEmpty())
                        {
                            errors.Add("Request type is set to `ACCESSION_NUMBER` but no `accessionNumber` defined.");
                        }
                        break;

                    default:
                        errors.Add($"'inputMetadata' does not yet support type '{InputMetadata?.Details?.Type}'.");
                        break;
                }
            }

            foreach (var input in InputResources)
            {
                if (input.Interface == InputInterfaceType.DicomWeb)
                {
                    CheckDicomWebConnectionDetails("inputResources", errors, input.ConnectionDetails);
                }
            }

            foreach (var output in OutputResources)
            {
                if (output.Interface == InputInterfaceType.DicomWeb)
                {
                    CheckDicomWebConnectionDetails("outputResources", errors, output.ConnectionDetails);
                }
            }

            details = string.Join(' ', errors);
            return errors.Count == 0;
        }

        private static void CheckDicomWebConnectionDetails(string source, List<string> errors, DicomWebConnectionDetails connection)
        {
            if (connection.AuthType != ConnectionAuthType.None && string.IsNullOrWhiteSpace(connection.AuthId))
            {
                errors.Add($"One of the '{source}' has authType of '{connection.AuthType:F}' but does not include a valid value for 'authId'");
            }

            if (!Uri.IsWellFormedUriString(connection.Uri, UriKind.Absolute))
            {
                errors.Add($"The provided URI '{connection.Uri}' is not well formed.");
            }
        }
    }
}