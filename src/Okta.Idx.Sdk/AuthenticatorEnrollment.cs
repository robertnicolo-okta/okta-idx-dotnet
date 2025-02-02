﻿// <copyright file="AuthenticatorEnrollment.cs" company="Okta, Inc">
// Copyright (c) 2020 - present Okta, Inc. All rights reserved.
// Licensed under the Apache 2.0 license. See the LICENSE file in the project root for full license information.
// </copyright>

namespace Okta.Idx.Sdk
{
    /// <summary>
    /// A class to represent the authenticator enrollment.
    /// </summary>
    public class AuthenticatorEnrollment : Resource, IAuthenticatorEnrollment
    {
        /// <inheritdoc/>
        public string Type => GetStringProperty("type");

        /// <inheritdoc/>
        public IAuthenticatorEnrollmentValue Value => GetResourceProperty<AuthenticatorEnrollmentValue>("value");
    }
}
