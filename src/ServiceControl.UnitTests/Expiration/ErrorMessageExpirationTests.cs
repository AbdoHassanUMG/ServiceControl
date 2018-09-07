namespace ServiceControl.UnitTests.Expiration
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.Composition.Hosting;
    using System.Configuration;
    using System.IO;
    using System.Threading.Tasks;
    using MessageFailures;
    using NUnit.Framework;
    using Raven.Abstractions;
    using Raven.Client;
    using Raven.Client.Embedded;
    using Raven.Client.Indexes;
    using Raven.Client.Linq;
    using ServiceBus.Management.Infrastructure.Settings;
    using ServiceControl.Infrastructure.RavenDB;
    using ServiceControl.Infrastructure.RavenDB.Expiration;

    [TestFixture]
    public class ErrorMessageExpirationTests
    {
        [Test]
        public async Task Should_expire_due_messages()
        {
            var auditRetentionValue = ConfigurationManager.AppSettings.Get("ServiceControl/AuditRetentionPeriod");
            var errorRetentionValue = ConfigurationManager.AppSettings.Get("ServiceControl/ErrorRetentionPeriod");
            
            ConfigurationManager.AppSettings.Set("ServiceControl/AuditRetentionPeriod", "00:00:00:01");
            ConfigurationManager.AppSettings.Set("ServiceControl/ErrorRetentionPeriod", "00:00:00:01");

            var settings = new Settings(validateConfiguration: false);

            var compiledIndexCacheDirectory = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(Path.GetTempFileName()));

            var store = new EmbeddableDocumentStore
            {
                Configuration =
                {
                    RunInUnreliableYetFastModeThatIsNotSuitableForProduction = true,
                    RunInMemory = true,
                    CompiledIndexCacheDirectory = compiledIndexCacheDirectory // RavenDB-2236
                },
                Conventions =
                {
                    SaveEnumsAsIntegers = true
                }
            };
            store.Configuration.Catalog.Catalogs.Add(new AssemblyCatalog(typeof(RavenBootstrapper).Assembly));
            store.Initialize();
            IndexCreation.CreateIndexes(typeof(RavenBootstrapper).Assembly, store);

            try
            {
                var yesterday = DateTime.UtcNow.AddDays(-1);
                SystemTime.UtcDateTime = () => yesterday;

                var operation = store.BulkInsert();
                for (var i = 0; i < 1000; i++)
                {
                    var id = Guid.NewGuid().ToString();
                    var failedMessage = new FailedMessage { Id = id, ProcessingAttempts = new List<FailedMessage.ProcessingAttempt> { new FailedMessage.ProcessingAttempt { MessageId = id }}, Status = FailedMessageStatus.Resolved };
                    operation.Store(failedMessage);
                }

                await operation.DisposeAsync();

                var tomorrow = DateTime.UtcNow.AddDays(1);
                SystemTime.UtcDateTime = () => tomorrow;

                operation = store.BulkInsert();
                for (var i = 0; i < 50; i++)
                {
                    var id = Guid.NewGuid().ToString();
                    var failedMessage = new FailedMessage { Id = id, ProcessingAttempts = new List<FailedMessage.ProcessingAttempt> { new FailedMessage.ProcessingAttempt { MessageId = id }}, Status = FailedMessageStatus.Resolved };
                    operation.Store(failedMessage);
                }

                await operation.DisposeAsync();

                SystemTime.UtcDateTime = () => DateTime.UtcNow;
                
                store.WaitForIndexing();

                ExpiredDocumentsCleaner.RunCleanup(2000, store.DocumentDatabase, settings);

                store.WaitForIndexing();

                using (var session = store.OpenAsyncSession())
                {
                    Assert.AreEqual(50, await session.Query<FailedMessage, ExpiryErrorMessageIndex>().Select(x => x.Id).CountAsync());
                }
            }
            finally
            {
                store.Dispose();
                Directory.Delete(compiledIndexCacheDirectory, true);
                ConfigurationManager.AppSettings.Set("ServiceControl/AuditRetentionPeriod", auditRetentionValue);
                ConfigurationManager.AppSettings.Set("ServiceControl/ErrorRetentionPeriod", errorRetentionValue);
            }
        }
    }
}