using Microsoft.Azure.Management.EventHub;
using Microsoft.Azure.Management.EventHub.Models;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Rest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ScaleDownEventHubsv2
{
    public static class ScaleDown
    {
        [FunctionName("ScaleDown")]
        public static void Run([TimerTrigger("0 */5 * * * *")]TimerInfo myTimer, ILogger log, ExecutionContext context)
        {
            log.LogInformation($"ScaleDown execution started");

            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            var creds = GetCredentials(config, log);

            var subs = GetSubscriptions(creds, log);

            log.LogInformation("Enumerating authorized subscriptions");
            foreach (var subscription in subs)
            {
                log.LogInformation($"Processing scaledown for subId:{subscription.SubscriptionId}, Name:{subscription.DisplayName}");
                ScaleDownNamespacesInSubscription(subscription.SubscriptionId, creds, log);
            }

            log.LogInformation("ScaleDown execution completed");
        }

        private static IEnumerable<ISubscription> GetSubscriptions(AzureCredentials credentials, ILogger log)
        {
            log.LogInformation("Authenticate as service principal");
            var azure = Azure.Authenticate(credentials);

            log.LogInformation("Get subscriptions");
            return azure.Subscriptions.List();
        }

        private static void ScaleDownNamespacesInSubscription(string subscriptionId, ServiceClientCredentials credentials, ILogger log)
        {
            var ehClient = new EventHubManagementClient(credentials)
            {
                SubscriptionId = subscriptionId
            };

            IEnumerable<EventhubNamespace> namespaces = null;
            try
            {
                namespaces = GetNamespacesForSubscription(ehClient, log);
            }
            catch (Exception e)
            {
                log.LogError(e, $"Error getting namespaces for subscription {subscriptionId}");
                return;
            }

            foreach (var ns in namespaces)
            {
                try
                {
                    ScaleDownNamespace(ns, ehClient, log);
                }
                catch (Exception e)
                {
                    log.LogError(e, $"Error scaling down namespace {ns.Namespace}");
                }
            }
        }

        private static IEnumerable<EventhubNamespace> GetNamespacesForSubscription(EventHubManagementClient ehClient, ILogger log)
        {
            log.LogInformation($"Getting namespaces for {ehClient.SubscriptionId}");
            var namespaces = ehClient.Namespaces.List().ToList();

            var nsList = new List<EventhubNamespace>();
            foreach (var ns in namespaces)
            {
                log.LogInformation($"Processing namespace {ns.Name} to extract RG and Throughput Units");

                var resourceGroupName = Regex.Match(ns.Id, @".*\/resourceGroups\/([\w-]+)\/providers.*").Groups[1].Value;

                int targetThroughputUnits = 1;
                if (ns.Tags.ContainsKey("ScaleDownTUs"))
                {
                    int.TryParse(ns.Tags["ScaleDownTUs"], out targetThroughputUnits);
                }

                nsList.Add(new EventhubNamespace(ehClient.SubscriptionId, resourceGroupName, ns.Name, targetThroughputUnits));
            }

            return nsList;
        }

        private static void ScaleDownNamespace(EventhubNamespace ns, EventHubManagementClient ehClient, ILogger log)
        {
            log.LogInformation($"ScaleDownNamespace for ns:{ns.Namespace}");

            var nsInfo = ehClient.Namespaces.Get(ns.ResourceGroup, ns.Namespace);
            if (nsInfo.Sku.Capacity <= ns.TargetThroughputUnits)
            {
                log.LogInformation($"Namespace:{ns.Namespace} in RG:{ns.ResourceGroup} already below target capacity (Current:{nsInfo.Sku.Capacity} Target:{ns.TargetThroughputUnits})");
                return;
            }

            var nsUpdate = new EHNamespace()
            {
                Sku = new Sku(nsInfo.Sku.Name, capacity: ns.TargetThroughputUnits)
            };

            log.LogInformation($"Updating Namespace:{ns.Namespace} in RG:{ns.ResourceGroup} from:{nsInfo.Sku.Capacity} to:{nsUpdate.Sku.Capacity}");
            ehClient.Namespaces.Update(ns.ResourceGroup, ns.Namespace, nsUpdate);
        }

        private static AzureCredentials GetCredentials(IConfigurationRoot config, ILogger log)
        {
            var clientId = config["ClientId"];
            var clientSecret = config["ClientSecret"];
            var tenantId = config["TenantId"];

            return new AzureCredentialsFactory().FromServicePrincipal(clientId, clientSecret, tenantId, AzureEnvironment.AzureGlobalCloud);
        }
    }
}
