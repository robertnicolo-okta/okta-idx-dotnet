﻿// <copyright file="IdxClient.cs" company="Okta, Inc">
// Copyright (c) 2020 - present Okta, Inc. All rights reserved.
// Licensed under the Apache 2.0 license. See the LICENSE file in the project root for full license information.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FlexibleConfiguration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Okta.Idx.Sdk.Configuration;
using Okta.Idx.Sdk.Extensions;
using Okta.Sdk.Abstractions;

namespace Okta.Idx.Sdk
{
    /// <summary>
    /// The IDX client.
    /// </summary>
    public class IdxClient : IIdxClient
    {
        private HttpClient httpClient;

        /// <summary>
        /// The <code>IDataStore</code> implementation to be used for making requests.
        /// </summary>
        private IDataStore _dataStore;

        /// <summary>
        /// The request context to be used when making requests.
        /// </summary>
        private RequestContext _requestContext;

        private ILogger _logger;

        static IdxClient()
        {
            System.AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="IdxClient"/> class.
        /// </summary>
/*        public IdxClient()
        {
        }*/

        /// <summary>
        /// Initializes a new instance of the <see cref="IdxClient"/> class using the specified <see cref="HttpClient"/>.
        /// </summary>
        /// <param name="configuration">
        /// The client configuration. If <c>null</c>, the library will attempt to load
        /// configuration from an <c>okta.yaml</c> file or environment variables.
        /// </param>
        /// <param name="httpClient">The HTTP client to use for requests to the Okta API.</param>
        /// <param name="logger">The logging interface to use, if any.</param>
        public IdxClient(
            IdxConfiguration configuration = null,
            HttpClient httpClient = null,
            ILogger logger = null)
        {
            Configuration = GetConfigurationOrDefault(configuration);
            IdxConfigurationValidator.Validate(Configuration);
            _logger = logger ?? NullLogger.Instance;

            var userAgentBuilder = new UserAgentBuilder("okta-idx-dotnet", typeof(IdxClient).GetTypeInfo().Assembly.GetName().Version);

            // TODO: Allow proxy configuration
            this.httpClient = httpClient ?? DefaultHttpClient.Create(
                connectionTimeout: null,
                proxyConfiguration: null,
                logger: _logger);

            var oktaBaseConfiguration = OktaConfigurationConverter.Convert(Configuration);
            var resourceTypeResolverFactory = new AbstractResourceTypeResolverFactory(ResourceTypeHelper.GetAllDefinedTypes(typeof(Resource)));
            var requestExecutor = new DefaultRequestExecutor(oktaBaseConfiguration, this.httpClient, _logger);
            var resourceFactory = new ResourceFactory(this, _logger, resourceTypeResolverFactory);

            _dataStore = new DefaultDataStore(
                requestExecutor,
                new DefaultSerializer(),
                resourceFactory,
                _logger,
                userAgentBuilder);
        }

        /// <summary>
        /// Builds and gets the IDX configuration.
        /// </summary>
        /// <param name="configuration">The IDX configuration.</param>
        /// <returns>The built configuration</returns>
        protected static IdxConfiguration GetConfigurationOrDefault(IdxConfiguration configuration)
        {
            string configurationFileRoot = Directory.GetCurrentDirectory();

            var homeOktaYamlLocation = HomePath.Resolve("~", ".okta", "okta.yaml");

            var applicationAppSettingsLocation = Path.Combine(configurationFileRoot ?? string.Empty, "appsettings.json");
            var applicationOktaYamlLocation = Path.Combine(configurationFileRoot ?? string.Empty, "okta.yaml");

            var configBuilder = new ConfigurationBuilder()
                .AddYamlFile(homeOktaYamlLocation, optional: true)
                .AddJsonFile(applicationAppSettingsLocation, optional: true)
                .AddYamlFile(applicationOktaYamlLocation, optional: true)
                .AddEnvironmentVariables("okta", "_", root: "okta")
                .AddEnvironmentVariables("okta_testing", "_", root: "okta")
                .AddObject(configuration, root: "okta:idx")
                .AddObject(configuration, root: "okta:testing")
                .AddObject(configuration);

            var compiledConfig = new IdxConfiguration();
            configBuilder.Build().GetSection("okta").GetSection("idx").Bind(compiledConfig);
            configBuilder.Build().GetSection("okta").GetSection("testing").Bind(compiledConfig);

            return compiledConfig;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="IdxClient"/> class.
        /// </summary>
        /// <param name="dataStore">The <see cref="IDataStore">DataStore</see> to use.</param>
        /// <param name="configuration">The client configuration.</param>
        /// <param name="requestContext">The request context, if any.</param>
        /// <remarks>This overload is used internally to create cheap copies of an existing client.</remarks>
        protected IdxClient(IDataStore dataStore, IdxConfiguration configuration, RequestContext requestContext)
        {
            _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
            Configuration = configuration;
            _requestContext = requestContext;
        }

        /// <summary>
        /// Gets or sets the Okta configuration.
        /// </summary>
        public IdxConfiguration Configuration { get; protected set; }

        private string GenerateSecureRandomString(int byteCount)
        {
            using (RandomNumberGenerator randomNumberGenerator = new RNGCryptoServiceProvider())
            {
                byte[] data = new byte[byteCount];
                randomNumberGenerator.GetBytes(data);

                return UrlFormatter.EncodeToBase64Url(data);
            }
        }

        /// <summary>
        /// Generates the <see cref="CodeChallenge"/> for use in PKCE
        /// </summary>
        private string GenerateCodeChallenge(string codeVerifier, out string codeChallengeMethod)
        {
            codeChallengeMethod = "S256";

            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));

                return UrlFormatter.EncodeToBase64Url(bytes);
            }
        }

        /// <summary>
        /// Calls the Idx interact endpoint to get an IDX context.
        /// </summary>
        /// <param name="state">Optional value to use as the state argument when initiating the authentication flow. This is used to provide contextual information to survive redirects.</param>
        /// <param name="cancellationToken">The cancellation token. Optional.</param>
        /// <returns>The IDX context.</returns>
        protected async Task<IIdxContext> InteractAsync(string state = null, CancellationToken cancellationToken = default)
        {
            // PKCE props
            state = state ?? GenerateSecureRandomString(16);
            var codeVerifier = GenerateSecureRandomString(86);
            var codeChallenge = GenerateCodeChallenge(codeVerifier, out var codeChallengeMethod);

            var payload = new Dictionary<string, string>();
            payload.Add("scope", string.Join(" ", Configuration.Scopes));
            payload.Add("client_id", Configuration.ClientId);

            // Add PKCE params and state
            payload.Add("code_challenge_method", codeChallengeMethod);
            payload.Add("code_challenge", codeChallenge);
            payload.Add("redirect_uri", Configuration.RedirectUri);
            payload.Add("state", state);

            var headers = new Dictionary<string, string>();
            headers.Add("Content-Type", HttpRequestContentBuilder.ContentTypeFormUrlEncoded);

            var request = new HttpRequest
            {
                Uri = $"{UrlHelper.EnsureTrailingSlash(Configuration.Issuer)}v1/interact",
                Payload = payload,
                Headers = headers,
            };

            var response = await PostAsync<InteractionHandleResponse>(
                request, cancellationToken).ConfigureAwait(false);

            return new IdxContext(codeVerifier, codeChallenge, codeChallengeMethod, response.InteractionHandle, state);
        }

        /// <summary>
        /// Calls the Idx introspect endpoint to get remediation steps.
        /// </summary>
        /// <param name="idxContext">The context to introspect.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{IIdxResponse}</returns>
        protected async Task<IIdxResponse> IntrospectAsync(IIdxContext idxContext, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await IntrospectAsync(idxContext.InteractionHandle, cancellationToken);
        }

        /// <summary>
        /// Calls the Idx introspect endpoint to get remediation steps.
        /// </summary>
        /// <param name="interactionHandle">The interaction handle.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{IIdxResponse}</returns>
        protected async Task<IIdxResponse> IntrospectAsync(string interactionHandle, CancellationToken cancellationToken = default(CancellationToken))
        {
            var payload = new IdxRequestPayload();
            payload.SetProperty("interactionHandle", interactionHandle);

            var oktaDomain = UrlHelper.GetOktaRootUrl(this.Configuration.Issuer);

            var uri = $"{UrlHelper.EnsureTrailingSlash(oktaDomain)}idp/idx/introspect";

            var headers = new Dictionary<string, string>
            {
                { "Accept", "application/ion+json; okta-version=1.0.0" },
            };

            var request = new HttpRequest
            {
                Uri = uri,
                Payload = payload,
                Headers = headers,
            };

            return await PostAsync<IdxResponse>(
                request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<SocialLoginResponse> StartSocialLoginAsync(string state = null, CancellationToken cancellationToken = default)
        {
            IIdxContext idxContext = await this.InteractAsync(state);
            IIdxResponse introspectResponse = await this.IntrospectAsync(idxContext);

            SocialLoginResponse socialLoginSettings = new SocialLoginResponse
            {
                Context = idxContext,
                IdpOptions = introspectResponse.Remediation?.RemediationOptions?
                    .Where(remediationOption => remediationOption.Name.Equals("redirect-idp"))
                    .Select(remediationOption => new IdpOption
                    {
                        State = idxContext.State,
                        InteractionHandle = idxContext.InteractionHandle,
                        Id = remediationOption.Idp.Id,
                        Name = remediationOption.Idp.Name,
                        Href = remediationOption.Href,
                    })
                    .ToArray(),
                Configuration = this.Configuration,
            };
            return socialLoginSettings;
        }

        public async Task<OktaTokens> RedeemInteractionCodeAsync(IdxContext idxContext, string interactionCode, Action<Exception> exceptionHandler = null, CancellationToken cancellationToken = default)
        {
            exceptionHandler = exceptionHandler ?? LogError;
            try
            {
                Uri issuerUri = new Uri(Configuration.Issuer);
                Uri tokenUri = new Uri(GetNormalizedUriString(issuerUri.ToString(), "v1/token"));
                HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, tokenUri);

                StringBuilder requestContent = new StringBuilder();
                this.AddParameter(requestContent, "grant_type", "interaction_code", false);
                this.AddParameter(requestContent, "client_id", Configuration.ClientId, true);
                if (!string.IsNullOrEmpty(Configuration.ClientSecret))
                {
                    this.AddParameter(requestContent, "client_secret", Configuration.ClientSecret, true);
                }

                this.AddParameter(requestContent, "interaction_code", interactionCode, true);
                this.AddParameter(requestContent, "code_verifier", idxContext.CodeVerifier, true);

                requestMessage.Content = new StringContent(requestContent.ToString(), Encoding.UTF8, "application/x-www-form-urlencoded");
                requestMessage.Headers.Add("Accept", "application/json");
                HttpResponseMessage responseMessage = await this.httpClient.SendAsync(requestMessage);
                string tokenResponseJson = await responseMessage.Content.ReadAsStringAsync();

                if (!responseMessage.IsSuccessStatusCode)
                {
                    exceptionHandler(new RedeemInteractionCodeException(tokenResponseJson));
                }

                return JsonConvert.DeserializeObject<OktaTokens>(tokenResponseJson);
            }
            catch (Exception exception)
            {
                exceptionHandler(new RedeemInteractionCodeException(exception));
            }

            return null;
        }

        protected virtual void LogError(Exception ex)
        {
            _logger.LogError(ex, ex.Message);
        }

        private void AddParameter(StringBuilder stringBuilder, string key, string value, bool ampersandPrefix = false)
        {
            if (ampersandPrefix)
            {
                stringBuilder.Append("&");
            }

            stringBuilder.Append($"{key}={value}");
        }

        private static string GetNormalizedUriString(string issuer, string resourceUri)
        {
            string normalized = issuer;
            if (IsRootOrgIssuer(issuer))
            {
                normalized = Path.Combine(normalized, "oauth2", resourceUri);
            }
            else
            {
                normalized = Path.Combine(normalized, resourceUri);
            }

            return normalized;
        }

        private static bool IsRootOrgIssuer(string issuerUri)
        {
            string path = new Uri(issuerUri).AbsolutePath;
            string[] splitUri = path.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (splitUri.Length >= 2 &&
            "oauth2".Equals(splitUri[0]) &&
            !string.IsNullOrEmpty(splitUri[1]))
            {
                return false;
            }

            return true;
        }

        /// <inheritdoc/>
        public IOktaClient CreateScoped(RequestContext requestContext)
            => new IdxClient(_dataStore, Configuration, requestContext);

        /// <inheritdoc/>
        public async Task<AuthenticationResponse> AuthenticateAsync(AuthenticationOptions authenticationOptions, CancellationToken cancellationToken = default)
        {
            var isPasswordFlow = !string.IsNullOrEmpty(authenticationOptions.Password);

            if (isPasswordFlow)
            {
                return await AuthenticateWithPasswordAsync(authenticationOptions, cancellationToken);
            }

            // assume users will log in with the authenticator they want

            var idxContext = await InteractAsync(cancellationToken: cancellationToken);
            var introspectResponse = await IntrospectAsync(idxContext, cancellationToken);

            // Common request payload
            var identifyRequest = new IdxRequestPayload();
            identifyRequest.StateHandle = introspectResponse.StateHandle;
            identifyRequest.SetProperty("identifier", authenticationOptions.Username);

            // TODO: Verify this flow doesn't require credentials right away
            var identifyResponse = await introspectResponse
                                            .Remediation
                                            .RemediationOptions
                                            .FirstOrDefault(x => x.Name == RemediationType.Identify)
                                            .ProceedAsync(identifyRequest, cancellationToken);

            if (IsRemediationsContainsRemediation(RemediationType.SelectAuthenticatorAuthenticate, identifyResponse))
            {
                return new AuthenticationResponse
                {
                    AuthenticationStatus = AuthenticationStatus.AwaitingAuthenticatorSelection,
                    Authenticators = identifyResponse.Authenticators.Value,
                };
            }
            else if (IsRemediationsContainsRemediation(RemediationType.ChallengeAuthenticator, identifyResponse))
            {
                throw new NotSupportedException("Challenge 2FA");
            }
            // TODO: Enroll might be a possible new branch here
            else
            {
                throw new UnexpectedRemediationException(
                        new List<string>
                        {
                            RemediationType.SelectAuthenticatorAuthenticate,
                            RemediationType.ChallengeAuthenticator,
                        },
                        introspectResponse);
            }
        }

        private async Task<AuthenticationResponse> AuthenticateWithPasswordAsync(AuthenticationOptions authenticationOptions, CancellationToken cancellationToken = default)
        {
            var idxContext = await InteractAsync(cancellationToken: cancellationToken);
            var introspectResponse = await IntrospectAsync(idxContext, cancellationToken);

            // Check if identify flow include credentials
            var isIdentifyInOneStep = IsRemediationRequireCredentials(RemediationType.Identify, introspectResponse);
            var isPasswordFlow = string.IsNullOrEmpty(authenticationOptions.Password);

            // Common request payload
            var identifyRequest = new IdxRequestPayload();
            identifyRequest.StateHandle = introspectResponse.StateHandle;
            identifyRequest.SetProperty("identifier", authenticationOptions.Username);

            if (isIdentifyInOneStep)
            {
                identifyRequest.SetProperty("credentials", new
                {
                    passcode = authenticationOptions.Password,
                });
            }

            var identifyResponse = await introspectResponse
                                            .Remediation
                                            .RemediationOptions
                                            .FirstOrDefault(x => x.Name == RemediationType.Identify)
                                            .ProceedAsync(identifyRequest, cancellationToken);

            if (isIdentifyInOneStep)
            {
                // We expect success
                if (!identifyResponse.IsLoginSuccess)
                {
                    // Verify if password expired
                    if (IsRemediationRequireCredentials(RemediationType.ReenrollAuthenticator, identifyResponse))
                    {
                        return new AuthenticationResponse
                        {
                            AuthenticationStatus = AuthenticationStatus.PasswordExpired,
                            IdxContext = idxContext,
                        };
                    }
                    // TODO: It needs test when enabling 2FA
                    else if (IsRemediationsContainsRemediation(RemediationType.ChallengeAuthenticator, identifyResponse))
                    {
                        //TODO
                        throw new NotSupportedException("Challenge 2FA");
                    }
                    else
                    {
                        throw new UnexpectedRemediationException(RemediationType.ReenrollAuthenticator, identifyResponse);
                    }
                }

                var tokenResponse = await identifyResponse.SuccessWithInteractionCode.ExchangeCodeAsync(idxContext, cancellationToken);

                return new AuthenticationResponse
                {
                    AuthenticationStatus = AuthenticationStatus.Success,
                    TokenInfo = tokenResponse,
                };
            }
            else
            {
                // We expect remediation has credentials now
                if (!IsRemediationRequireCredentials(RemediationType.ChallengeAuthenticator, identifyResponse)
                    && !IsRemediationsContainsRemediation(RemediationType.SelectAuthenticatorAuthenticate, identifyResponse))
                {
                    throw new UnexpectedRemediationException(
                        new List<string>
                        {
                            RemediationType.ChallengeAuthenticator,
                            RemediationType.SelectAuthenticatorAuthenticate,
                        }, identifyResponse);
                }

                var sendPasswordResponse = identifyResponse;

                if (IsRemediationsContainsRemediation(RemediationType.SelectAuthenticatorAuthenticate, identifyResponse))
                {
                    var passwordAuthenticator = identifyResponse
                                                 .Authenticators
                                                 .Value
                                                 .FirstOrDefault(x => x.Key == AuthenticatorType.Password.ToIdxKeyString());

                    if (passwordAuthenticator == null)
                    {
                        throw new OktaException("Password is not available for authentication. Please review your policies.");
                    }

                    var selectAuthenticatorRequest = new IdxRequestPayload();
                    selectAuthenticatorRequest.StateHandle = identifyResponse.StateHandle;
                    selectAuthenticatorRequest.SetProperty(
                        "authenticator",
                        new
                        {
                            id = passwordAuthenticator.Id,
                        });

                    var selectAuthenticatorResponse = await identifyResponse
                                                  .Remediation
                                                  .RemediationOptions
                                                  .FirstOrDefault(x => x.Name == RemediationType.SelectAuthenticatorAuthenticate)
                                                  .ProceedAsync(selectAuthenticatorRequest, cancellationToken);

                    sendPasswordResponse = selectAuthenticatorResponse;
                }

                var challengeRequest = new IdxRequestPayload();
                challengeRequest.StateHandle = identifyResponse.StateHandle;
                challengeRequest.SetProperty("credentials", new
                {
                    passcode = authenticationOptions.Password,
                });

                var challengeResponse = await sendPasswordResponse
                                              .Remediation
                                              .RemediationOptions
                                              .FirstOrDefault(x => x.Name == RemediationType.ChallengeAuthenticator)
                                              .ProceedAsync(challengeRequest, cancellationToken);

                if (!challengeResponse.IsLoginSuccess)
                {
                    // Verify if password expired
                    if (IsRemediationRequireCredentials(RemediationType.ReenrollAuthenticator, challengeResponse))
                    {
                        return new AuthenticationResponse
                        {
                            AuthenticationStatus = AuthenticationStatus.PasswordExpired,
                            IdxContext = idxContext,
                        };
                    }
                    // TODO: It needs test when enabling 2FA
                    else if (IsRemediationsContainsRemediation(RemediationType.ChallengeAuthenticator, identifyResponse))
                    {
                        //TODO
                        throw new NotSupportedException("Challenge 2FA");
                    }
                    else
                    {
                        throw new UnexpectedRemediationException(RemediationType.ReenrollAuthenticator, challengeResponse);
                    }
                }

                var tokenResponse = await challengeResponse.SuccessWithInteractionCode.ExchangeCodeAsync(idxContext, cancellationToken);

                return new AuthenticationResponse
                {
                    AuthenticationStatus = AuthenticationStatus.Success,
                    TokenInfo = tokenResponse,
                };
            }
        }

        /// <inheritdoc/>
        public async Task<AuthenticationResponse> ChangePasswordAsync(ChangePasswordOptions changePasswordOptions, IIdxContext idxContext, CancellationToken cancellationToken = default)
        {
            // Re-entry flow with context
            var introspectResponse = await IntrospectAsync(idxContext);
            var currentRemediationType = RemediationType.Unknown;

            // Check if flow is password expiration or forgot password, otherwise throw
            if (introspectResponse.ContainsRemediationOption(RemediationType.ReenrollAuthenticator))
            {
                currentRemediationType = RemediationType.ReenrollAuthenticator;
            }
            else if (introspectResponse.ContainsRemediationOption(RemediationType.ResetAuthenticator))
            {
                currentRemediationType = RemediationType.ResetAuthenticator;
            }
            else
            {
                if (currentRemediationType == RemediationType.ReenrollAuthenticator &&
                    !IsRemediationRequireCredentials(RemediationType.ReenrollAuthenticator, introspectResponse))
                {
                    throw new UnexpectedRemediationException(RemediationType.ReenrollAuthenticator, introspectResponse);
                }
                else
                {
                    throw new UnexpectedRemediationException(
                        new List<string>
                        {
                            RemediationType.ReenrollAuthenticator,
                            RemediationType.ResetAuthenticator,
                        },
                        introspectResponse);
                }
            }

            var resetAuthenticatorRequest = new IdxRequestPayload
            {
                StateHandle = introspectResponse.StateHandle,
            };

            resetAuthenticatorRequest.SetProperty("credentials", new
            {
                passcode = changePasswordOptions.NewPassword,
            });

            // Reset password
            var resetPasswordResponse = await introspectResponse
                                              .ProceedWithRemediationOptionAsync(currentRemediationType, resetAuthenticatorRequest, cancellationToken);

            if (resetPasswordResponse.IsLoginSuccess)
            {
                var tokenResponse = await resetPasswordResponse.SuccessWithInteractionCode.ExchangeCodeAsync(idxContext, cancellationToken);

                return new AuthenticationResponse
                {
                    AuthenticationStatus = AuthenticationStatus.Success,
                    TokenInfo = tokenResponse,
                };
            }
            else
            {
                throw new UnexpectedRemediationException(RemediationType.SuccessWithInteractionCode, resetPasswordResponse);
            }
        }

        /// <inheritdoc/>
        public async Task<AuthenticationResponse> RecoverPasswordAsync(RecoverPasswordOptions recoverPasswordOptions, CancellationToken cancellationToken = default)
        {
            var idxContext = await InteractAsync(cancellationToken: cancellationToken);
            var introspectResponse = await IntrospectAsync(idxContext, cancellationToken);

            // Common request payload
            var identifyRequest = new IdxRequestPayload
            {
                StateHandle = introspectResponse.StateHandle,
            };
            identifyRequest.SetProperty("identifier", recoverPasswordOptions.Username);

            // Send username
            var identifyResponse = await introspectResponse.ProceedWithRemediationOptionAsync(RemediationType.Identify, identifyRequest, cancellationToken);

            // Get available authenticators
            var recoveryRequest = new IdxRequestPayload
            {
                StateHandle = identifyResponse.StateHandle,
            };

            var recoveryResponse = await identifyResponse
                                        .CurrentAuthenticatorEnrollment
                                        .Value
                                        .Recover
                                        .ProceedAsync(recoveryRequest, cancellationToken);

            var recoveryAuthenticators = recoveryResponse
                .Authenticators
                .Value;

            return new AuthenticationResponse
            {
                IdxContext = idxContext,
                AuthenticationStatus = AuthenticationStatus.AwaitingAuthenticatorSelection,
                Authenticators = recoveryAuthenticators,
            };
        }

        /// <inheritdoc/>
        public async Task<AuthenticationResponse> SelectRecoveryAuthenticatorAsync(SelectAuthenticatorOptions selectAuthenticatorOptions, IIdxContext idxContext, CancellationToken cancellationToken = default)
        {
            // Re-entry flow with context
            var introspectResponse = await IntrospectAsync(idxContext, cancellationToken);

            var recoveryRequest = new IdxRequestPayload
            {
                StateHandle = introspectResponse.StateHandle,
            };

            var recoveryResponse = await introspectResponse
                                        .CurrentAuthenticatorEnrollment
                                        .Value
                                        .Recover
                                        .ProceedAsync(recoveryRequest, cancellationToken);

            var recoveryAuthenticator = recoveryResponse
                                           .Authenticators
                                           .Value
                                           .Where(x => x.Id == selectAuthenticatorOptions.AuthenticatorId)
                                           .FirstOrDefault();

            if (recoveryAuthenticator == null)
            {
                throw new OktaException($"Authenticator not found. Verify that you have the selected authenticator enabled for your application.");
            }

            var selectAuthenticatorRequest = new IdxRequestPayload
            {
                StateHandle = recoveryResponse.StateHandle,
            };

            selectAuthenticatorRequest.SetProperty("authenticator", new
            {
                id = recoveryAuthenticator.Id,
            });

            var selectRecoveryAuthenticatorRemediationOption = await recoveryResponse
                    .ProceedWithRemediationOptionAsync(RemediationType.SelectAuthenticatorAuthenticate, selectAuthenticatorRequest, cancellationToken);

            if (!selectRecoveryAuthenticatorRemediationOption.ContainsRemediationOption(RemediationType.ChallengeAuthenticator))
            {
                throw new UnexpectedRemediationException(RemediationType.ChallengeAuthenticator, selectRecoveryAuthenticatorRemediationOption);
            }

            return new AuthenticationResponse
            {
                AuthenticationStatus = AuthenticationStatus.AwaitingAuthenticatorVerification,
                IdxContext = idxContext,
            };
        }

        /// <inheritdoc/>
        public async Task<AuthenticationResponse> VerifyAuthenticatorAsync(VerifyAuthenticatorOptions verifyAuthenticatorOptions, IIdxContext idxContext, CancellationToken cancellationToken = default)
        {
            // Re-entry flow with context
            var introspectResponse = await IntrospectAsync(idxContext);
            var currentRemediationType = RemediationType.Unknown;

            // Check if flow is challenge authenticator or enroll authenticator, otherwise throw
            if (introspectResponse.ContainsRemediationOption(RemediationType.ChallengeAuthenticator))
            {
                currentRemediationType = RemediationType.ChallengeAuthenticator;
            }
            else if (introspectResponse.ContainsRemediationOption(RemediationType.EnrollAuthenticator))
            {
                currentRemediationType = RemediationType.EnrollAuthenticator;
            }
            else
            {
                if (currentRemediationType == RemediationType.EnrollAuthenticator &&
                    !IsRemediationRequireCredentials(RemediationType.EnrollAuthenticator, introspectResponse))
                {
                    throw new UnexpectedRemediationException(RemediationType.EnrollAuthenticator, introspectResponse);
                }
                else if (currentRemediationType == RemediationType.ChallengeAuthenticator &&
                    !IsRemediationRequireCredentials(RemediationType.ChallengeAuthenticator, introspectResponse))
                {
                    throw new UnexpectedRemediationException(RemediationType.ChallengeAuthenticator, introspectResponse);
                }
                else
                {
                    throw new UnexpectedRemediationException(
                        new List<string>
                        {
                            RemediationType.ChallengeAuthenticator,
                            RemediationType.EnrollAuthenticator,
                        },
                        introspectResponse);
                }
            }

            var challengeAuthenticatorRequest = new IdxRequestPayload
            {
                StateHandle = introspectResponse.StateHandle,
            };
            challengeAuthenticatorRequest.SetProperty("credentials", new
            {
                passcode = verifyAuthenticatorOptions.Code,
            });

            var challengeAuthenticatorResponse = await introspectResponse
                .ProceedWithRemediationOptionAsync(currentRemediationType, challengeAuthenticatorRequest, cancellationToken);

            var isResetAuthenticator = challengeAuthenticatorResponse.ContainsRemediationOption(RemediationType.ResetAuthenticator);
            var isAuthenticatorEnroll = challengeAuthenticatorResponse.ContainsRemediationOption(RemediationType.SelectAuthenticatorEnroll);
            // TODO: Assume default authenticators?
            //var canSkip = challengeAuthenticatorResponse.Remediation.RemediationOptions.Any(x => x.Name == RemediationType.Skip);

            //if (canSkip)
            //{
            //    var skipRequest = new IdxRequestPayload
            //    {
            //        StateHandle = challengeAuthenticatorResponse.StateHandle,
            //    };

            //    var skipResponse = await challengeAuthenticatorResponse
            //                            .Remediation
            //                            .RemediationOptions
            //                            .FirstOrDefault(x => x.Name == RemediationType.Skip)
            //                            .ProceedAsync(skipRequest, cancellationToken);

            //    if (skipResponse.IsLoginSuccess)
            //    {
            //        var tokenResponse = await skipResponse.SuccessWithInteractionCode.ExchangeCodeAsync(idxContext, cancellationToken);

            //        return new AuthenticationResponse
            //        {
            //            AuthenticationStatus = AuthenticationStatus.Success,
            //            TokenInfo = tokenResponse,
            //        };
            //    }
            //}
            if (challengeAuthenticatorResponse.IsLoginSuccess)
            {
                var tokenResponse = await challengeAuthenticatorResponse.SuccessWithInteractionCode.ExchangeCodeAsync(idxContext, cancellationToken);

                return new AuthenticationResponse
                {
                    AuthenticationStatus = AuthenticationStatus.Success,
                    TokenInfo = tokenResponse,
                };
            }

            if (isResetAuthenticator)
            {
                return new AuthenticationResponse
                {
                    AuthenticationStatus = AuthenticationStatus.AwaitingPasswordReset,
                    IdxContext = idxContext,
                };
            }
            else if (isAuthenticatorEnroll)
            {
                return new AuthenticationResponse
                {
                    AuthenticationStatus = AuthenticationStatus.AwaitingAuthenticatorEnrollment,
                    Authenticators = challengeAuthenticatorResponse.Authenticators.Value,
                    IdxContext = idxContext,
                };
            }

            throw new UnexpectedRemediationException(
                      new List<string>
                      {
                            RemediationType.ResetAuthenticator,
                            RemediationType.SelectAuthenticatorEnroll,
                      },
                      challengeAuthenticatorResponse);
        }

        /// <inheritdoc/>
        public async Task RevokeTokensAsync(TokenType tokenType, string token, CancellationToken cancellationToken = default)
        {
            var payload = new Dictionary<string, string>
            {
                { "client_id", Configuration.ClientId },
            };

            if (Configuration.IsConfidentialClient)
            {
                payload.Add("client_secret", Configuration.ClientSecret);
            }

            payload.Add("token_type_hint", tokenType.ToTokenHintString());
            payload.Add("token", token);

            var headers = new Dictionary<string, string>
            {
                { "Content-Type", HttpRequestContentBuilder.ContentTypeFormUrlEncoded },
            };

            var request = new HttpRequest
            {
                Uri = $"{UrlHelper.EnsureTrailingSlash(Configuration.Issuer)}v1/revoke",
                Payload = payload,
                Headers = headers,
            };

            await PostAsync<Resource>(request, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<AuthenticationResponse> RegisterAsync(UserProfile userProfile, CancellationToken cancellationToken = default)
        {
            var idxContext = await InteractAsync(cancellationToken: cancellationToken);
            var introspectResponse = await IntrospectAsync(idxContext, cancellationToken);

            var enrollRequest = new IdxRequestPayload
            {
                StateHandle = introspectResponse.StateHandle,
            };

            // choose enroll option
            var enrollProfileResponse = await introspectResponse.ProceedWithRemediationOptionAsync(RemediationType.SelectEnrollProfile, enrollRequest, cancellationToken);

            var enrollNewProfileRequest = new IdxRequestPayload
            {
                StateHandle = enrollProfileResponse.StateHandle,
            };
            enrollNewProfileRequest.SetProperty("userProfile", userProfile);

            var enrollNewProfileResponse = await enrollProfileResponse.ProceedWithRemediationOptionAsync(RemediationType.EnrollProfile, enrollNewProfileRequest, cancellationToken);

            if (!enrollNewProfileResponse.ContainsRemediationOption(RemediationType.SelectAuthenticatorEnroll))
            {
                throw new UnexpectedRemediationException(RemediationType.SelectAuthenticatorEnroll, enrollNewProfileResponse);
            }

            return new AuthenticationResponse
            {
                IdxContext = idxContext,
                AuthenticationStatus = AuthenticationStatus.AwaitingAuthenticatorEnrollment,
                Authenticators = enrollNewProfileResponse.Authenticators.Value,
            };
        }

        /// <inheritdoc/>
        public async Task<AuthenticationResponse> EnrollAuthenticatorAsync(EnrollAuthenticatorOptions enrollAuthenticatorOptions, IIdxContext idxContext, CancellationToken cancellationToken = default)
        {
            var selectAuthenticatorRequest = new IdxRequestPayload();
            selectAuthenticatorRequest.SetProperty("authenticator", new
            {
                id = enrollAuthenticatorOptions.AuthenticatorId,
            });

            return await EnrollAuthenticatorAsync(selectAuthenticatorRequest, idxContext, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<AuthenticationResponse> EnrollAuthenticatorAsync(EnrollPhoneAuthenticatorOptions enrollAuthenticatorOptions, IIdxContext idxContext, CancellationToken cancellationToken = default)
        {
            var selectAuthenticatorRequest = new IdxRequestPayload();
            selectAuthenticatorRequest.SetProperty("authenticator", new
            {
                id = enrollAuthenticatorOptions.AuthenticatorId,
                methodType = enrollAuthenticatorOptions.MethodType.ToString().ToLower(),
                phoneNumber = enrollAuthenticatorOptions.PhoneNumber,
            });

            return await EnrollAuthenticatorAsync(selectAuthenticatorRequest, idxContext, cancellationToken);
        }

        private async Task<AuthenticationResponse> EnrollAuthenticatorAsync(IdxRequestPayload selectAuthenticatorRequest, IIdxContext idxContext, CancellationToken cancellationToken = default)
        {
            // Re-entry flow with context
            var introspectResponse = await IntrospectAsync(idxContext, cancellationToken);
            selectAuthenticatorRequest.StateHandle = introspectResponse.StateHandle;

            var selectAuthenticatorEnrollRemediationOption = introspectResponse
                                                             .Remediation
                                                             .RemediationOptions
                                                             .FirstOrDefault(x => x.Name == RemediationType.SelectAuthenticatorEnroll);

            // Verify if enroll is required
            if (selectAuthenticatorEnrollRemediationOption == null)
            {
                throw new UnexpectedRemediationException(RemediationType.SelectAuthenticatorEnroll, introspectResponse);
            }

            var selectAuthenticatorResponse = await selectAuthenticatorEnrollRemediationOption.ProceedAsync(selectAuthenticatorRequest, cancellationToken);
            var currentRemediationType = RemediationType.Unknown;
            var status = AuthenticationStatus.AwaitingAuthenticatorVerification;

            // Check if flow is challenge authenticator or enroll authenticator, otherwise throw
            if (selectAuthenticatorResponse.Remediation.RemediationOptions.Any(x => x.Name == RemediationType.AuthenticatorEnrollmentData))
            {
                currentRemediationType = RemediationType.AuthenticatorEnrollmentData;
                status = AuthenticationStatus.AwaitingAuthenticatorEnrollmentData;
            }
            else if (selectAuthenticatorResponse.Remediation.RemediationOptions.Any(x => x.Name == RemediationType.EnrollAuthenticator))
            {
                currentRemediationType = RemediationType.EnrollAuthenticator;
            }

            if (currentRemediationType != RemediationType.EnrollAuthenticator &&
                    currentRemediationType != RemediationType.AuthenticatorEnrollmentData)
            {
                throw new UnexpectedRemediationException(
                    new List<string>
                    {
                            RemediationType.AuthenticatorEnrollmentData,
                            RemediationType.EnrollAuthenticator,
                    },
                    selectAuthenticatorResponse);
            }

            return new AuthenticationResponse
            {
                IdxContext = idxContext,
                AuthenticationStatus = status,
            };
        }

        private static bool IsRemediationRequireCredentials(string remediationOptionName, IIdxResponse idxResponse)
        {
            var jToken = JToken.Parse(idxResponse.GetRaw());

            var credentialsObj = jToken.SelectToken($"$.remediation.value[?(@.name == '{remediationOptionName}')].value[?(@.name == 'credentials')]");

            return credentialsObj != null;
        }

        private static bool IsRemediationsContainsRemediation(string remediationOptionName, IIdxResponse idxResponse)
        {
            var jToken = JToken.Parse(idxResponse.GetRaw());

            var credentialsObj = jToken.SelectToken($"$.remediation.value[?(@.name == '{remediationOptionName}')]");

            return credentialsObj != null;
        }

        /// <summary>
        /// Creates a new <see cref="CollectionClient{T}"/> given an initial HTTP request.
        /// </summary>
        /// <typeparam name="T">The collection client item type.</typeparam>
        /// <param name="initialRequest">The initial HTTP request.</param>
        /// <returns>The collection client.</returns>
        protected CollectionClient<T> GetCollectionClient<T>(HttpRequest initialRequest)
            where T : IResource
            => new CollectionClient<T>(_dataStore, initialRequest, _requestContext);

        /// <inheritdoc/>
        public Task<T> GetAsync<T>(string href, CancellationToken cancellationToken = default(CancellationToken))
            where T : BaseResource, new()
            => GetAsync<T>(new HttpRequest { Uri = href }, cancellationToken);

        /// <inheritdoc/>
        public async Task<T> GetAsync<T>(HttpRequest request, CancellationToken cancellationToken = default(CancellationToken))
            where T : BaseResource, new()
        {
            var response = await _dataStore.GetAsync<T>(request, _requestContext, cancellationToken).ConfigureAwait(false);
            return response?.Payload;
        }

        /// <inheritdoc/>
        public CollectionClient<T> GetCollection<T>(string href)
            where T : IResource
            => GetCollection<T>(new HttpRequest
            {
                Uri = href,
            });

        /// <inheritdoc/>
        public CollectionClient<T> GetCollection<T>(HttpRequest request)
            where T : IResource
            => GetCollectionClient<T>(request);

        /// <inheritdoc/>
        public Task PostAsync(string href, object model, CancellationToken cancellationToken = default(CancellationToken))
            => PostAsync(new HttpRequest { Uri = href, Payload = model }, cancellationToken);

        /// <inheritdoc/>
        public Task<TResponse> PostAsync<TResponse>(string href, object model, CancellationToken cancellationToken = default(CancellationToken))
            where TResponse : BaseResource, new()
            => PostAsync<TResponse>(new HttpRequest { Uri = href, Payload = model }, cancellationToken);

        /// <inheritdoc/>
        public Task PostAsync(HttpRequest request, CancellationToken cancellationToken = default(CancellationToken))
            => PostAsync<BaseResource>(request, cancellationToken);

        /// <inheritdoc/>
        public async Task<TResponse> PostAsync<TResponse>(HttpRequest request, CancellationToken cancellationToken = default(CancellationToken))
            where TResponse : BaseResource, new()
        {
            var response = await _dataStore.PostAsync<TResponse>(request, _requestContext, cancellationToken).ConfigureAwait(false);
            return response?.Payload;
        }

        /// <inheritdoc/>
        public Task PutAsync(string href, object model, CancellationToken cancellationToken = default(CancellationToken))
            => PutAsync(new HttpRequest { Uri = href, Payload = model }, cancellationToken);

        /// <inheritdoc/>
        public Task<TResponse> PutAsync<TResponse>(string href, object model, CancellationToken cancellationToken = default(CancellationToken))
            where TResponse : BaseResource, new()
            => PutAsync<TResponse>(new HttpRequest { Uri = href, Payload = model }, cancellationToken);

        /// <inheritdoc/>
        public Task PutAsync(HttpRequest request, CancellationToken cancellationToken = default(CancellationToken))
            => PutAsync<BaseResource>(request, cancellationToken);

        /// <inheritdoc/>
        public async Task<TResponse> PutAsync<TResponse>(HttpRequest request, CancellationToken cancellationToken = default(CancellationToken))
            where TResponse : BaseResource, new()
        {
            var response = await _dataStore.PutAsync<TResponse>(request, _requestContext, cancellationToken).ConfigureAwait(false);
            return response?.Payload;
        }

        /// <inheritdoc/>
        public Task DeleteAsync(string href, CancellationToken cancellationToken = default(CancellationToken))
            => DeleteAsync(new HttpRequest { Uri = href }, cancellationToken);

        /// <inheritdoc/>
        public Task DeleteAsync(HttpRequest request, CancellationToken cancellationToken = default(CancellationToken))
            => _dataStore.DeleteAsync(request, _requestContext, cancellationToken);

        /// <inheritdoc/>
        public async Task<TResponse> SendAsync<TResponse>(HttpRequest request, HttpVerb httpVerb, CancellationToken cancellationToken = default)
            where TResponse : BaseResource, new()
        {
            switch (httpVerb)
            {
                case HttpVerb.Get:
                    return await GetAsync<TResponse>(request, cancellationToken).ConfigureAwait(false);
                case HttpVerb.Post:
                    return await PostAsync<TResponse>(request, cancellationToken).ConfigureAwait(false);
                case HttpVerb.Put:
                    return await PutAsync<TResponse>(request, cancellationToken).ConfigureAwait(false);
                case HttpVerb.Delete:
                    await DeleteAsync(request, cancellationToken).ConfigureAwait(false);
                    return null;
                default:
                    return await GetAsync<TResponse>(request, cancellationToken).ConfigureAwait(false);
            }
        }

    }
}
