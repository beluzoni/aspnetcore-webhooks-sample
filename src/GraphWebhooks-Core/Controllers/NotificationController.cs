﻿/*
 *  Copyright (c) Microsoft. All rights reserved. Licensed under the MIT license.
 *  See LICENSE in the source repository root for complete license information.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph;
using GraphWebhooks_Core.Helpers;
using GraphWebhooks_Core.Models;
using GraphWebhooks_Core.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using GraphWebhooks_Core.Helpers.Interfaces;
using System.Linq;
using Microsoft.Extensions.Options;
using GraphWebhooks_Core.Infrastructure;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;

namespace GraphWebhooks_Core.Controllers
{

    public class NotificationController : Controller
    {
        private readonly ISubscriptionStore subscriptionStore;
        private readonly IHubContext<NotificationHub> notificationHub;
        private readonly ILogger logger;
        readonly ITokenAcquisition tokenAcquisition;
        private readonly IOptions<MicrosoftIdentityOptions> identityOptions;
        private readonly KeyVaultManager keyVaultManager;
        private readonly IOptions<DownstreamApiSettings> appSettings;
        private readonly IOptions<SubscriptionOptions> subscriptionOptions;
        private readonly NotificationService notificationService = new NotificationService();
        private readonly GraphServiceClient graphServiceClient;

        public NotificationController(ISubscriptionStore subscriptionStore,
                                      IHubContext<NotificationHub> notificationHub,
                                      ILogger<NotificationController> logger,
                                      ITokenAcquisition tokenAcquisition,
                                      IOptions<MicrosoftIdentityOptions> identityOptions,
                                      KeyVaultManager keyVaultManager,
                                      IOptions<DownstreamApiSettings> appSettings,
                                      IOptions<SubscriptionOptions> subscriptionOptions,
                                      GraphServiceClient graphServiceClient)
        {
            this.subscriptionStore = subscriptionStore;
            this.notificationHub = notificationHub;
            this.logger = logger;
            this.tokenAcquisition = tokenAcquisition;
            this.identityOptions = identityOptions ?? throw new ArgumentNullException(nameof(identityOptions));
            this.keyVaultManager = keyVaultManager ?? throw new ArgumentNullException(nameof(keyVaultManager));
            this.appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
            this.subscriptionOptions = subscriptionOptions ?? throw new ArgumentNullException(nameof(subscriptionOptions));
            this.graphServiceClient = graphServiceClient ?? throw new ArgumentNullException(nameof(graphServiceClient));
        }

        [Authorize]
        public ActionResult LoadView(string id)
        {
            ViewBag.CurrentSubscriptionId = id; // Passing this along so we can delete it later.
            return View("Notification");
        }
        public ActionResult LoadViewAppOnly(string id)
        {
            ViewBag.CurrentSubscriptionId = id; // Passing this along so we can delete it later.
            return View("Notification");
        }

        // The notificationUrl endpoint that's registered with the webhook subscription.
        [HttpPost]
        [AuthorizeForScopes(ScopeKeySection = "SubscriptionSettings:Scope")]
        public async Task<IActionResult> Listen([FromQuery]string validationToken = null)
        {
            if (string.IsNullOrEmpty(validationToken))
            {
                try
                {
                    // Parse the received notifications.
                    var plainNotifications = new Dictionary<string, ChangeNotification>();
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                    var collection = await JsonSerializer.DeserializeAsync<ChangeNotificationCollection>(Request.Body, options);
                    foreach (var notification in collection.Value.Where(x => x.EncryptedContent == null))
                    {
                        SubscriptionStore subscription = subscriptionStore.GetSubscriptionInfo(notification.SubscriptionId.Value);

                        // Verify the current client state matches the one that was sent.
                        if (notification.ClientState == subscription.ClientState)
                        {
                            // Just keep the latest notification for each resource. No point pulling data more than once.
                            plainNotifications[notification.Resource] = notification;
                        }
                    }

                    if (plainNotifications.Count > 0)
                    {
                        // Query for the changed messages. 
                        await GetChangedMessagesAsync(plainNotifications.Values);
                    }

                    if (collection.ValidationTokens != null && collection.ValidationTokens.Any())
                    { // we're getting notifications with resource data and we should validate tokens and decrypt data
                        TokenValidator tokenValidator = new TokenValidator(identityOptions.Value.TenantId, new[] { identityOptions.Value.ClientId });
                        bool areValidationTokensValid = (await Task.WhenAll(
                            collection.ValidationTokens.Select(x => tokenValidator.ValidateToken(x))).ConfigureAwait(false))
                            .Aggregate((x, y) => x && y);
                        if (areValidationTokensValid)
                        {
                            List<NotificationViewModel> notificationsToDisplay = new List<NotificationViewModel>();
                            foreach (var notificationItem in collection.Value.Where(x => x.EncryptedContent != null))
                            {
                                string decryptedpublisherNotification =
                                Decryptor.Decrypt(
                                    notificationItem.EncryptedContent.Data,
                                    notificationItem.EncryptedContent.DataKey,
                                    notificationItem.EncryptedContent.DataSignature,
                                    await keyVaultManager.GetDecryptionCertificate().ConfigureAwait(false));

                                notificationsToDisplay.Add(new NotificationViewModel(decryptedpublisherNotification));
                            }

                            await notificationService.SendNotificationToClient(notificationHub, notificationsToDisplay);
                            return Accepted();
                        }
                        else
                        {
                            return Unauthorized("Token Validation failed");
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"ParsingNotification: { ex.Message }");

                    // Still return a 202 so the service doesn't resend the notification.
                }
                return Accepted();
            }
            else
            {
                // Validate the new subscription by sending the token back to Microsoft Graph.
                // This response is required for each subscription.
                return Content(WebUtility.HtmlEncode(validationToken));
            }
        }

        // Get information about the changed messages and send to browser via SignalR.
        // A production application would typically queue a background job for reliability.
        private async Task GetChangedMessagesAsync(IEnumerable<ChangeNotification> notifications)
        {
            List<NotificationViewModel> notificationsToDisplay = new List<NotificationViewModel>();
            foreach (var notification in notifications)
            {
                SubscriptionStore subscription = subscriptionStore.GetSubscriptionInfo(notification.SubscriptionId.Value);

                // Set the claims for ObjectIdentifier and TenantId, and              
                // use the above claims for the current HttpContext
                if (!string.IsNullOrEmpty(subscription.UserId))
                    HttpContext.User = ClaimsPrincipalFactory.FromTenantIdAndObjectId(subscription.TenantId, subscription.UserId);

                if (notification.Resource.Contains("/message", StringComparison.InvariantCultureIgnoreCase))
                {
                    var request = new MessageRequest(graphServiceClient.BaseUrl + "/" + notification.Resource, graphServiceClient, null);
                    try
                    {
                        var responseValue = await (string.IsNullOrEmpty(subscription.UserId) ? request.WithAppOnly() : request).GetAsync();
                        notificationsToDisplay.Add(new NotificationViewModel(new
                        {
                            From = responseValue?.From?.EmailAddress?.Address,
                            responseValue?.Subject,
                            SentDateTime = responseValue?.SentDateTime.HasValue ?? false ? responseValue?.SentDateTime.Value.ToString() : string.Empty,
                            To = responseValue?.ToRecipients?.Select(x => x.EmailAddress.Address)?.ToList(),
                        }));
                    }
                    catch (ServiceException se)
                    {
                        string errorMessage = se.Error.Message;
                        string requestId = se.Error.InnerError?.AdditionalData["request-id"]?.ToString();
                        string requestDate = se.Error.InnerError?.AdditionalData["date"]?.ToString();

                        logger.LogError($"RetrievingMessages: { errorMessage } Request ID: { requestId } Date: { requestDate }");
                    }
                }
                else
                {
                    notificationsToDisplay.Add(new NotificationViewModel(notification.Resource));
                }
            }

            if (notificationsToDisplay.Count > 0)
            {
                await notificationService.SendNotificationToClient(notificationHub, notificationsToDisplay);
            }
        }
    }
}