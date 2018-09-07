namespace ServiceControl.UnitTests.Infrastructure.RavenDB
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.Composition.Hosting;
    using System.Configuration;
    using System.IO;
    using System.Threading.Tasks;
    using MessageAuditing;
    using NUnit.Framework;
    using Raven.Client;
    using Raven.Client.Embedded;
    using Raven.Client.Indexes;
    using Raven.Client.Linq;
    using ServiceBus.Management.Infrastructure.Settings;
    using ServiceControl.Infrastructure.RavenDB;
    using ServiceControl.Infrastructure.RavenDB.Expiration;

    [TestFixture]
    public class AuditMessageExpirationTests
    {
        [Test]
        public async Task Should_expired_due_messages()
        {
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

            try
            {
                IndexCreation.CreateIndexes(typeof(RavenBootstrapper).Assembly, store);

                var yesterday = DateTime.UtcNow.AddDays(-1);

                var operation = store.BulkInsert();
                for (var i = 0; i < 1000; i++)
                {
                    var id = Guid.NewGuid().ToString();
                    var messageMetadata = new Dictionary<string, object>
                    {
                        {"MessageId", id},
                        {"BodyNotStored", true},
                        {"OtherStuff", "NotInteresting"}
                    };
                    operation.Store(new ProcessedMessage {Id = id, ProcessedAt = yesterday, MessageMetadata = messageMetadata});
                }

                await operation.DisposeAsync();

                var tomorrow = DateTime.UtcNow.AddDays(1);

                operation = store.BulkInsert();
                for (var i = 0; i < 50; i++)
                {
                    var id = Guid.NewGuid().ToString();
                    var messageMetadata = new Dictionary<string, object>
                    {
                        {"MessageId", id},
                        {"BodyNotStored", true},
                        {"OtherStuff", "NotInteresting"}
                    };
                    operation.Store(new ProcessedMessage {Id = id, ProcessedAt = tomorrow, MessageMetadata = messageMetadata});
                }

                await operation.DisposeAsync();

                store.WaitForIndexing();

                ExpiredDocumentsCleaner.RunCleanup(2000, store.DocumentDatabase, settings);

                store.WaitForIndexing();

                using (var session = store.OpenAsyncSession())
                {
                    Assert.AreEqual(50, await session.Query<ProcessedMessage, ExpiryProcessedMessageIndex>().Select(x => x.Id).CountAsync());
                }
            }
            finally
            {
                store.Dispose();
                Directory.Delete(compiledIndexCacheDirectory, true);
            }
        }
    }
}