﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Services;
using System.Linq;
using System.Runtime.Versioning;
using System.ServiceModel.Web;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;
using NuGet;
using NugetGallery;

namespace NuGetGallery
{
    public class V2Feed : FeedServiceBase<V2FeedPackage>
    {
        private const int FeedVersion = 2;

        public V2Feed()
        {

        }

        public V2Feed(IEntitiesContext entities, IEntityRepository<Package> repo, IConfiguration configuration)
            : base(entities, repo, configuration)
        {

        }

        protected override FeedContext<V2FeedPackage> CreateDataSource()
        {
            return new FeedContext<V2FeedPackage>
            {
                Packages = PackageRepo.GetAll()
                                      .WithoutVersionSort()
                                      .ToV2FeedPackageQuery(Configuration.GetSiteRoot(UseHttps()))
            };
        }

        public static void InitializeService(DataServiceConfiguration config)
        {
            InitializeServiceBase(config);
            config.SetServiceOperationAccessRule("GetUpdates", ServiceOperationRights.AllRead);
        }

        [WebGet]
        public IQueryable<V2FeedPackage> Search(string searchTerm, string targetFramework, bool includePrerelease)
        {
            var packages = PackageRepo.GetAll()
                                      .Include(p => p.Authors)
                                      .Include(p => p.PackageRegistration)
                                      .Include(p => p.PackageRegistration.Owners)
                                      .Where(p => p.Listed);


            // this may not be the best idea over time.
            //var packageVersions = Cache.Get("V2Feed-Search",
            //      DateTime.Now.AddMinutes(Cache.DEFAULT_CACHE_TIME_MINUTES),
            //      () => packages.ToList()
            //      );

            //return SearchCore(packageVersions.AsQueryable(), searchTerm, targetFramework, includePrerelease)
            //        .ToV2FeedPackageQuery(GetSiteRoot())
            //        .ToList()
            //        .AsQueryable();

            var packageVersions = Cache.Get(string.Format("V2Feed-Search-{0}", searchTerm.to_lower()),
                   DateTime.Now.AddMinutes(Cache.DEFAULT_CACHE_TIME_MINUTES),
                   () => SearchCore(packages, searchTerm, targetFramework).ToV2FeedPackageQuery(GetSiteRoot()).ToList()
                 );

            if (!includePrerelease)
            {
                return packageVersions.Where(p => !p.IsPrerelease).AsQueryable();
            }

            return packageVersions.AsQueryable();
            
            //return Cache.Get(string.Format("V2Feed-Search-{0}-{1}-{2}", searchTerm, targetFramework, includePrerelease),
            //       DateTime.Now.AddMinutes(Cache.DEFAULT_CACHE_TIME_MINUTES),
            //       () => SearchCore(packages, searchTerm, targetFramework, includePrerelease).ToV2FeedPackageQuery(GetSiteRoot())
            //               .ToList().AsQueryable());
        }

        [WebGet]
        public IQueryable<V2FeedPackage> FindPackagesById(string id)
        {
            return Cache.Get(string.Format("V2Feed-FindPackagesById-{0}", id.to_lower()), 
                    DateTime.Now.AddMinutes(Cache.DEFAULT_CACHE_TIME_MINUTES), 
                    () => PackageRepo.GetAll().Include(p => p.PackageRegistration)
                            .Where(p => p.PackageRegistration.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                            .ToV2FeedPackageQuery(GetSiteRoot())
                            .ToList().AsQueryable());

            //return packages.AsQueryable();

            //return PackageRepo.GetAll().Include(p => p.PackageRegistration)
            //                           .Where(p => p.PackageRegistration.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            //                           .ToV2FeedPackageQuery(GetSiteRoot());
        }

        [WebGet]
        public IQueryable<V2FeedPackage> GetUpdates(string packageIds, string versions, bool includePrerelease, bool includeAllVersions, string targetFrameworks)
        {
            if (String.IsNullOrEmpty(packageIds) || String.IsNullOrEmpty(versions))
            {
                return Enumerable.Empty<V2FeedPackage>().AsQueryable();
            }

            var idValues = packageIds.Trim().Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            var versionValues = versions.Trim().Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            var targetFrameworkValues = String.IsNullOrEmpty(targetFrameworks) ? null :
                                                                                 targetFrameworks.Split('|').Select(VersionUtility.ParseFrameworkName).ToList();

            if ((idValues.Length == 0) || (idValues.Length != versionValues.Length))
            {
                // Exit early if the request looks invalid
                return Enumerable.Empty<V2FeedPackage>().AsQueryable();
            }

            Dictionary<string, SemanticVersion> versionLookup = new Dictionary<string, SemanticVersion>(idValues.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < idValues.Length; i++)
            {
                var id = idValues[i];
                SemanticVersion version;
                SemanticVersion currentVersion;

                if (SemanticVersion.TryParse(versionValues[i], out currentVersion) &&
                     (!versionLookup.TryGetValue(id, out version) || (currentVersion > version)))
                {
                    // If we've never added the package to lookup or we encounter the same id but with a higher version, then choose the higher version.
                    versionLookup[id] = currentVersion;
                }
            }

            var packages = PackageRepo.GetAll()
                              .Include(p => p.PackageRegistration)
                              .Include(p => p.SupportedFrameworks)
                              .Where(p => p.Listed && (includePrerelease || !p.IsPrerelease) && idValues.Contains(p.PackageRegistration.Id))
                              .OrderBy(p => p.PackageRegistration.Id);

            //GetUpdates(string packageIds, string versions, bool includePrerelease, bool includeAllVersions, string targetFrameworks
            return Cache.Get(string.Format("V2Feed-GetUpdates-{0}-{1}-{2}-{3}", string.Join("|", idValues).to_lower(), string.Join("|", versionValues).to_lower(), includePrerelease, includeAllVersions), 
                             DateTime.Now.AddMinutes(Cache.DEFAULT_CACHE_TIME_MINUTES), 
                             () => GetUpdates(packages, versionLookup, targetFrameworkValues, includeAllVersions).AsQueryable().ToV2FeedPackageQuery(GetSiteRoot()).ToList().AsQueryable());

            //return searchResults.AsQueryable();
            //return GetUpdates(packages, versionLookup, targetFrameworkValues, includeAllVersions).AsQueryable().ToV2FeedPackageQuery(GetSiteRoot());
        }

        private static IEnumerable<Package> GetUpdates(IEnumerable<Package> packages,
                                                       Dictionary<string, SemanticVersion> versionLookup,
                                                       IEnumerable<FrameworkName> targetFrameworkValues,
                                                       bool includeAllVersions)
        {
            var updates = packages.AsEnumerable()
                                  .Where(p =>
                                  {
                                      // For each package, if the version is higher than the client version and we satisty the target framework, return it.
                                      // TODO: We could optimize for the includeAllVersions case here by short circuiting the operation once we've encountered the highest version
                                      // for a given Id
                                      var version = SemanticVersion.Parse(p.Version);
                                      SemanticVersion clientVersion;
                                      if (versionLookup.TryGetValue(p.PackageRegistration.Id, out clientVersion))
                                      {
                                          var supportedPackageFrameworks = p.SupportedFrameworks.Select(f => f.FrameworkName);

                                          return (version > clientVersion) &&
                                                  (targetFrameworkValues == null || targetFrameworkValues.Any(s => VersionUtility.IsCompatible(s, supportedPackageFrameworks)));
                                      }
                                      return false;
                                  });

            if (!includeAllVersions)
            {
                return updates.GroupBy(p => p.PackageRegistration.Id)
                              .Select(g => g.OrderByDescending(p => SemanticVersion.Parse(p.Version)).First());
            }
            return updates;
        }

        public override Uri GetReadStreamUri(
           object entity,
           DataServiceOperationContext operationContext)
        {
            var package = (V2FeedPackage)entity;
            var urlHelper = new UrlHelper(new RequestContext(HttpContext, new RouteData()));

            string url = urlHelper.PackageDownload(FeedVersion, package.Id, package.Version);

            return new Uri(url, UriKind.Absolute);
        }

        private string GetSiteRoot()
        {
            return Configuration.GetSiteRoot(UseHttps());
        }
    }
}
