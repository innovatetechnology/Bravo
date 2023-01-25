namespace Sqlbi.Bravo.Infrastructure.Services.DaxTemplate
{
    using Dax.Template;
    using Dax.Template.Exceptions;
    using Dax.Template.Model;
    using Microsoft.AnalysisServices.AdomdClient;
    using Sqlbi.Bravo.Infrastructure.Extensions;
    using Sqlbi.Bravo.Infrastructure.Helpers;
    using Sqlbi.Bravo.Infrastructure.Security.Policies;
    using Sqlbi.Bravo.Models;
    using Sqlbi.Bravo.Models.ManageDates;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;

    internal class DaxTemplateManager
    {
        private static bool _cacheInitialized = false;
        private static readonly object _cacheSyncLock = new();

        public const string SqlbiTemplateAnnotation = "SQLBI_Template";
        public const string SqlbiTemplateAnnotationDatesValue = "Dates";
        public const string SqlbiTemplateAnnotationHolidaysValue = "Holidays";
        public const string SqlbiTemplateTableAnnotation = "SQLBI_TemplateTable";
        public const string SqlbiTemplateTableAnnotationDateValue = "Date";
        public const string SqlbiTemplateTableAnnotationDateAutoTemplateValue = "DateAutoTemplate";
        public const string SqlbiTemplateTableAnnotationHolidaysValue = "Holidays";
        public const string SqlbiTemplateTableAnnotationHolidaysDefinitionValue = "HolidaysDefinition";

        private const string TemplateEmbeddedResourcePrefix = "Sqlbi.Bravo.Assets.ManageDates.Templates.";
        //private const string SchemaEmbeddedResourcePrefix = "Sqlbi.Bravo.Assets.TemplateDevelopment.Schemas.";

        internal static readonly string CachePath = Path.Combine(AppEnvironment.ApplicationTempPath, @"ManageDates\Templates");
        internal static readonly string UserPath = Path.Combine(AppEnvironment.ApplicationDataPath, @"ManageDates\Templates");

        public DaxTemplateManager()
        {
            InitializeCache();
        }

        public Package GetPackage(string path)
        {
            try
            {
                var package = Package.LoadFromFile(path);
                return package;
            }
            catch (TemplateException ex)
            {
                TelemetryHelper.TrackException(ex);
                throw;
            }
        }

        public IEnumerable<Package> GetPackages()
        {
            if (BravoPolicies.Current.BuiltInTemplatesEnabledPolicy == PolicyStatus.Forced)
            {
                if (!BravoPolicies.Current.BuiltInTemplatesEnabled)
                    return Array.Empty<Package>();
            }

            try
            {
                var files = Package.FindTemplateFiles(CachePath);
                var packages = files.Select(Package.LoadFromFile).ToArray();

                return packages;
            }
            catch (TemplateException ex)
            {
                TelemetryHelper.TrackException(ex);
                throw;
            }
        }

        public ModelChanges GetPreviewChanges(DateConfiguration configuration, int previewRows, TabularConnectionWrapper connection, CancellationToken cancellationToken)
        {
            try
            {
                var package = configuration.LoadPackage();
                var changes = GetPreviewChanges(package, previewRows, connection, cancellationToken);

                return changes;
            }
            catch (Exception ex) when (ex is TemplateException || ex is AdomdException)
            {
                TelemetryHelper.TrackException(ex);
                throw;
            }
        }

        public ModelChanges GetPreviewChanges(Package package, int previewRows, TabularConnectionWrapper connection, CancellationToken cancellationToken)
        {
            try
            {
                var engine = new Engine(package);

                engine.ApplyTemplates(connection.Model, cancellationToken);
                try
                {
                    var changes = Engine.GetModelChanges(connection.Model, cancellationToken);

                    if (previewRows > 0)
                    {
                        using var adomdConnection = connection.CreateAdomdConnection();
                        changes.PopulatePreview(adomdConnection, connection.Model, previewRows, cancellationToken);
                    }

                    return changes;
                }
                finally
                {
                    connection.Model.UndoLocalChanges();
                }
            }
            catch (Exception ex) when (ex is TemplateException || ex is AdomdException)
            {
                TelemetryHelper.TrackException(ex);
                throw;
            }
        }

        public void ApplyConfiguration(DateConfiguration configuration, TabularConnectionWrapper connection, CancellationToken cancellationToken)
        {
            try
            {
                var package = configuration.LoadPackage();
                var engine = new Engine(package);

                engine.ApplyTemplates(connection.Model, cancellationToken);
                configuration.SerializeTo(connection.Model);
                connection.Model.SaveChanges().ThrowOnError();
            }
            catch (TemplateException ex)
            {
                TelemetryHelper.TrackException(ex);
                throw;
            }
        }

        //private IEnumerable<(string Name, Stream Content)> GetSchemaFiles()
        //{
        //    var files = GetResources(prefix: SchemaEmbeddedResourcePrefix);
        //    return files;
        //}

        private IEnumerable<(string Name, Stream Content)> GetTemplates()
        {
            var files = GetResources(prefix: TemplateEmbeddedResourcePrefix);
            return files;
        }

        private IEnumerable<(string Name, Stream Content)> GetResources(string prefix)
        {
            var assembly = typeof(Program).Assembly;
            var resourceNames = assembly.GetManifestResourceNames().Where((n) => n.StartsWith(prefix, StringComparison.InvariantCulture));

            foreach (var resourceName in resourceNames)
            {
                var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is not null)
                {
                    var name = resourceName.Remove(0, prefix.Length);
                    yield return (Name: name, Content: stream);
                } 
            }
        }

        private void InitializeCache()
        {
            if (!_cacheInitialized)
            {
                lock (_cacheSyncLock)
                {
                    if (!_cacheInitialized)
                    {
                        if (Directory.Exists(CachePath))
                            Directory.Delete(CachePath, recursive: true);

                        Directory.CreateDirectory(CachePath);
                        
                        if (Directory.Exists(UserPath))
                        {
                            // If the path exists then we use the templates from this folder instead of using the built-in default templates - this is for testing/debug purpose only
                            foreach (var userFile in Directory.EnumerateFiles(UserPath))
                            {
                                var userFileName = Path.GetFileName(userFile);
                                var cacheFile = Path.Combine(CachePath, userFileName);

                                File.Copy(userFile, cacheFile);
                            }
                        }
                        else
                        {
                            foreach (var (name, content) in GetTemplates())
                            {
                                var path = Path.Combine(CachePath, name);
                                using var stream = File.Create(path);

                                content.CopyTo(stream);
                            }
                        }

                        _cacheInitialized = true;
                    }
                }
            }
        }
    }
}
