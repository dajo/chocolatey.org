﻿// Copyright 2011 - Present RealDimensions Software, LLC, the original 
// authors/contributors from ChocolateyGallery
// at https://github.com/chocolatey/chocolatey.org,
// and the authors/contributors of NuGetGallery 
// at https://github.com/NuGet/NuGetGallery
//  
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Data;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Linq;
using WebBackgrounder;

namespace NuGetGallery
{
    public interface IEntitiesContext
    {
        int SaveChanges();
        DbSet<T> Set<T>() where T : class;

        IDbSet<CuratedFeed> CuratedFeeds { get; set; }
        IDbSet<CuratedPackage> CuratedPackages { get; set; }
        IDbSet<PackageRegistration> PackageRegistrations { get; set; }
        IDbSet<User> Users { get; set; }
    }

    public class EntitiesContext : DbContext, IWorkItemsContext, IEntitiesContext
    {
        public EntitiesContext() : base("NuGetGallery")
        {
            InitializeCustomOptions();
        }

        /// <summary>
        ///   Initializes the custom options.
        /// </summary>
        protected void InitializeCustomOptions()
        {
            // defaults for quick reference
            //Configuration.LazyLoadingEnabled = true;
            //Configuration.ProxyCreationEnabled = true;
            //Configuration.AutoDetectChangesEnabled = true;
            //Configuration.ValidateOnSaveEnabled = true;

            Configuration.LazyLoadingEnabled = false;
            //Configuration.ValidateOnSaveEnabled = false;
            var adapter = this as IObjectContextAdapter;
            if (adapter != null)
            {
                var objectContext = adapter.ObjectContext;
                objectContext.CommandTimeout = 110; // value in seconds
            }
       }

        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>().HasKey(u => u.Key);

            modelBuilder.Entity<User>()
                        .HasMany<EmailMessage>(u => u.Messages)
                        .WithRequired(em => em.ToUser)
                        .HasForeignKey(em => em.ToUserKey);

            modelBuilder.Entity<User>()
                        .HasMany<Role>(u => u.Roles)
                        .WithMany(r => r.Users)
                        .Map(c => c.ToTable("UserRoles").MapLeftKey("UserKey").MapRightKey("RoleKey"));

            modelBuilder.Entity<Role>().HasKey(u => u.Key);

            modelBuilder.Entity<EmailMessage>().HasKey(em => em.Key);

            modelBuilder.Entity<EmailMessage>()
                        .HasOptional<User>(em => em.FromUser)
                        .WithMany()
                        .HasForeignKey(em => em.FromUserKey);

            modelBuilder.Entity<PackageRegistration>().HasKey(pr => pr.Key);
            
            modelBuilder.Entity<PackageRegistration>()
                        .HasOptional<User>(e => e.TrustedBy)
                        .WithMany()
                        .HasForeignKey(e => e.TrustedById)
                        .WillCascadeOnDelete(false);

            modelBuilder.Entity<PackageRegistration>()
                        .HasMany<User>(pr => pr.Owners)
                        .WithMany()
                        .Map(
                            c =>
                            c.ToTable("PackageRegistrationOwners").MapLeftKey("PackageRegistrationKey").MapRightKey("UserKey"));

            modelBuilder.Entity<PackageRegistration>()
                        .HasMany<Package>(pr => pr.Packages)
                        .WithRequired(p => p.PackageRegistration)
                        .HasForeignKey(p => p.PackageRegistrationKey);

            modelBuilder.Entity<Package>().HasKey(p => p.Key);

            modelBuilder.Entity<Package>()
                        .HasMany<PackageAuthor>(p => p.Authors)
                        .WithRequired(pa => pa.Package)
                        .HasForeignKey(pa => pa.PackageKey);

            modelBuilder.Entity<Package>()
                        .HasMany<PackageStatistics>(p => p.DownloadStatistics)
                        .WithRequired(ps => ps.Package)
                        .HasForeignKey(ps => ps.PackageKey);

            modelBuilder.Entity<PackageStatistics>().HasKey(ps => ps.Key);

            modelBuilder.Entity<Package>()
                        .HasMany<PackageDependency>(p => p.Dependencies)
                        .WithRequired(pd => pd.Package)
                        .HasForeignKey(pd => pd.PackageKey);

            modelBuilder.Entity<PackageDependency>().HasKey(pd => pd.Key);

            modelBuilder.Entity<Package>()
                        .HasOptional<User>(e => e.ReviewedBy)
                        .WithMany()
                        .HasForeignKey(e => e.ReviewedById)
                        .WillCascadeOnDelete(false);

            modelBuilder.Entity<PackageAuthor>().HasKey(pa => pa.Key);

            modelBuilder.Entity<GallerySetting>().HasKey(gs => gs.Key);

            modelBuilder.Entity<WorkItem>().HasKey(wi => wi.Id);

            modelBuilder.Entity<PackageOwnerRequest>().HasKey(por => por.Key);

            modelBuilder.Entity<PackageFramework>().HasKey(pf => pf.Key);

            modelBuilder.Entity<CuratedFeed>().HasKey(cf => cf.Key);

            modelBuilder.Entity<CuratedFeed>()
                        .HasMany<CuratedPackage>(cf => cf.Packages)
                        .WithRequired(cp => cp.CuratedFeed)
                        .HasForeignKey(cp => cp.CuratedFeedKey);

            modelBuilder.Entity<CuratedFeed>()
                        .HasMany<User>(cf => cf.Managers)
                        .WithMany()
                        .Map(c => c.ToTable("CuratedFeedManagers").MapLeftKey("CuratedFeedKey").MapRightKey("UserKey"));

            modelBuilder.Entity<CuratedPackage>().HasKey(cp => cp.Key);

            modelBuilder.Entity<CuratedPackage>().HasRequired(cp => cp.PackageRegistration);

            OnChocolateyModelCreating(modelBuilder);
        }

        public IDbSet<CuratedFeed> CuratedFeeds { get; set; }
        public IDbSet<CuratedPackage> CuratedPackages { get; set; }
        public IDbSet<PackageRegistration> PackageRegistrations { get; set; }
        public IDbSet<User> Users { get; set; }
        public IDbSet<WorkItem> WorkItems { get; set; }

        protected void OnChocolateyModelCreating(DbModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserSiteProfile>().HasKey(e => e.Key);

            modelBuilder.Entity<Package>()
                        .HasMany<PackageFile>(p => p.Files)
                        .WithRequired(pf => pf.Package)
                        .HasForeignKey(pf => pf.PackageKey);

            modelBuilder.Entity<PackageFile>().HasKey(pa => pa.Key);
        }
    }
}
