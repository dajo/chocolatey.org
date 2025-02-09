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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Security.Principal;
using System.Transactions;
using System.Web;
using System.Web.Mvc;
using Elmah;
using NuGet;
using PoliteCaptcha;

namespace NuGetGallery
{
    public partial class PackagesController : Controller
    {
        // TODO: add support for URL-based package submission
        // TODO: add support for uploading logos and screenshots
        // TODO: improve validation summary emphasis

        private readonly IPackageService packageSvc;
        private readonly IUploadFileService uploadFileSvc;
        private readonly IUserService userSvc;
        private readonly IMessageService messageService;
        private readonly IAutomaticallyCuratePackageCommand autoCuratedPackageCmd;
        public IConfiguration Configuration { get; set; }

        public PackagesController(
            IPackageService packageSvc, IUploadFileService uploadFileSvc, IUserService userSvc, IMessageService messageService, IAutomaticallyCuratePackageCommand autoCuratedPackageCmd,
            IConfiguration configuration)
        {
            this.packageSvc = packageSvc;
            this.uploadFileSvc = uploadFileSvc;
            this.userSvc = userSvc;
            this.messageService = messageService;
            this.autoCuratedPackageCmd = autoCuratedPackageCmd;
            Configuration = configuration;
        }

        [Authorize]
        public virtual ActionResult UploadPackage()
        {
            var currentUser = userSvc.FindByUsername(GetIdentity().Name);

            using (var existingUploadFile = uploadFileSvc.GetUploadFile(currentUser.Key))
            {
                if (existingUploadFile != null) return RedirectToRoute(RouteName.VerifyPackage);
            }

            return View("~/Views/Packages/UploadPackage.cshtml");
        }

        [Authorize, HttpPost, ValidateAntiForgeryToken]
        public virtual ActionResult UploadPackage(HttpPostedFileBase uploadFile)
        {
            var currentUser = userSvc.FindByUsername(GetIdentity().Name);

            using (var existingUploadFile = uploadFileSvc.GetUploadFile(currentUser.Key))
            {
                if (existingUploadFile != null) return new HttpStatusCodeResult(409, "Cannot upload file because an upload is already in progress.");
            }

            if (uploadFile == null)
            {
                ModelState.AddModelError(String.Empty, Strings.UploadFileIsRequired);
                return View("~/Views/Packages/UploadPackage.cshtml");
            }

            if (!Path.GetExtension(uploadFile.FileName).Equals(Constants.NuGetPackageFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(String.Empty, Strings.UploadFileMustBeNuGetPackage);
                return View("~/Views/Packages/UploadPackage.cshtml");
            }

            IPackage nuGetPackage;
            try
            {
                using (var uploadStream = uploadFile.InputStream)
                {
                    nuGetPackage = ReadNuGetPackage(uploadStream);
                }
            }
            catch
            {
                ModelState.AddModelError(String.Empty, Strings.FailedToReadUploadFile);
                return View("~/Views/Packages/UploadPackage.cshtml");
            }

            var packageRegistration = packageSvc.FindPackageRegistrationById(nuGetPackage.Id, useCache: false);
            if (packageRegistration != null && !packageRegistration.Owners.AnySafe(x => x.Key == currentUser.Key))
            {
                ModelState.AddModelError(String.Empty, String.Format(CultureInfo.CurrentCulture, Strings.PackageIdNotAvailable, packageRegistration.Id));
                return View("~/Views/Packages/UploadPackage.cshtml");
            }

            var package = packageSvc.FindPackageByIdAndVersion(nuGetPackage.Id, nuGetPackage.Version.ToStringSafe());
            if (package != null)
            {
                switch (package.Status)
                {
                    case PackageStatusType.Rejected:
                        ModelState.AddModelError(String.Empty, string.Format("This package has been {0} and can no longer be submitted.", package.Status.GetDescriptionOrValue().ToLower()));
                        return View("~/Views/Packages/UploadPackage.cshtml");
                    case PackageStatusType.Submitted:
                        //continue on 
                        break;
                    default:
                        ModelState.AddModelError(String.Empty, String.Format(CultureInfo.CurrentCulture, Strings.PackageExistsAndCannotBeModified, package.PackageRegistration.Id, package.Version));
                        return View("~/Views/Packages/UploadPackage.cshtml");
                }
            }

            using (var fileStream = nuGetPackage.GetStream())
            {
                uploadFileSvc.SaveUploadFile(currentUser.Key, fileStream);
            }

            return RedirectToRoute(RouteName.VerifyPackage);
        }

        public virtual ActionResult DisplayPackage(string id, string version)
        {
            var package = packageSvc.FindPackageByIdAndVersion(id, version);

            if (package == null) return PackageNotFound(id, version);
            var model = new DisplayPackageViewModel(package);

            return View("~/Views/Packages/DisplayPackage.cshtml", model);
        }

        [Authorize, HttpPost]
        public virtual ActionResult DisplayPackage(string id, string version, FormCollection form)
        {
            if (!ModelState.IsValid) return DisplayPackage(id, version);
            var currentUser = userSvc.FindByUsername(GetIdentity().Name);

            var package = packageSvc.FindPackageByIdAndVersion(id, version, allowPrerelease: true, useCache: false);

            if (package == null) return PackageNotFound(id, version);
            var model = new DisplayPackageViewModel(package);

            var packageRegistration = package.PackageRegistration;
            var isMaintainer = packageRegistration.Owners.AnySafe(x => x.Key == currentUser.Key);
            var isModerationRole = User.IsInAnyModerationRole() && !isMaintainer;
            var isModerator = User.IsModerator() && !isMaintainer;

            if (packageRegistration != null && !isMaintainer && !isModerationRole)
            {
                ModelState.AddModelError(String.Empty, String.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "maintain"));
                return View("~/Views/Packages/DisplayPackage.cshtml", model);
            }

            if (!ModelState.IsValid) return View("~/Views/Packages/DisplayPackage.cshtml", model);

            var status = PackageStatusType.Unknown;
            if (isMaintainer)
            {
                status = package.Status;
            }
            else
            {
                try
                {
                    status = (PackageStatusType)Enum.Parse(typeof(PackageStatusType), form["Status"]);
                }
                catch (Exception ex)
                {
                    // Log but swallow the exception
                    ErrorSignal.FromCurrentContext().Raise(ex);
                }
            }

            // maintainers and reviewers cannot change the current status
            if (User.IsReviewer() || isMaintainer) status = package.Status;

            if (package.Status != PackageStatusType.Unknown && status == PackageStatusType.Unknown)
            {
                ModelState.AddModelError(String.Empty, "A package cannot be moved into unknown status.");
                return View("~/Views/Packages/DisplayPackage.cshtml", model);
            }
            if (package.Status == PackageStatusType.Unknown && status == PackageStatusType.Submitted)
            {
                ModelState.AddModelError(String.Empty, "A package cannot be moved from unknown to submitted status.");
                return View("~/Views/Packages/DisplayPackage.cshtml", model);
            }

            if (User.IsReviewer() && status != PackageStatusType.Submitted)
            {
                ModelState.AddModelError(String.Empty, "A reviewer can only comment/submit in submitted status.");
                return View("~/Views/Packages/DisplayPackage.cshtml", model);
            }

            var comments = form["ReviewComments"];
            var newComments = form["NewReviewComments"];
            bool sendEmail = form["SendEmail"] != null;
            bool trustedPackage = form["IsTrusted"] == "true,false";
            bool maintainerReject = form["MaintainerReject"] == "true";
            bool rerunTests = form["RerunTests"] == "true";

            if (maintainerReject && string.IsNullOrWhiteSpace(newComments))
            {
                ModelState.AddModelError(String.Empty, "In order to reject a package version, you must provide comments indicating why it is being rejected.");
                return View("~/Views/Packages/DisplayPackage.cshtml", model);
            }

            if (isMaintainer && string.IsNullOrWhiteSpace(newComments))
            {
                ModelState.AddModelError(String.Empty, "You need to provide comments.");
                return View("~/Views/Packages/DisplayPackage.cshtml", model);
            }

            if (isMaintainer && maintainerReject)
            {
                status = PackageStatusType.Rejected;
            }

            if (rerunTests)
            {
                packageSvc.ResetPackageTestStatus(package);
            }

            // could be null if no moderation has happened yet
            var moderator = isModerationRole ? currentUser : package.ReviewedBy;

            packageSvc.ChangePackageStatus(package, status, comments, newComments, currentUser, moderator, sendEmail, isModerationRole ? PackageSubmittedStatusType.Waiting : PackageSubmittedStatusType.Responded);

            if (isModerator)
            {
                packageSvc.ChangeTrustedStatus(package, trustedPackage, moderator);
            }

            //grab updated package
            package = packageSvc.FindPackageByIdAndVersion(id, version, allowPrerelease: true, useCache: false);
            model = new DisplayPackageViewModel(package);

            TempData["Message"] = "Changes to package status have been saved.";

            return View("~/Views/Packages/DisplayPackage.cshtml", model);
        }

        public virtual ActionResult ListPackages(string q, string sortOrder = null, int page = 1, bool prerelease = true, bool moderatorQueue = false)
        {
            if (page < 1) page = 1;

            IEnumerable<Package> packageVersions = packageSvc.GetPackagesForListing(prerelease);
            IEnumerable<Package> packagesToShow = new List<Package>();

            if (moderatorQueue)
            {
                var unknownStatus = PackageStatusType.Unknown.GetDescriptionOrValue();

                //This is going to be fun. Unknown status ones would be listed, but sometimes a few might slip through the cracks if a maintainer unlists a package.
                // A user can just email us to catch those though.
                packageVersions = packageVersions.Where(p => !p.IsPrerelease).Where(p => p.StatusForDatabase == unknownStatus || p.StatusForDatabase == null);
            }

            q = (q ?? "").Trim();

            if (String.IsNullOrEmpty(sortOrder))
            {
                // Determine the default sort order. If no query string is specified, then the sortOrder is DownloadCount
                // If we are searching for something, sort by relevance.
                sortOrder = q.IsEmpty() ? Constants.PopularitySortOrder : Constants.RelevanceSortOrder;
            }

            int totalHits = 0;
            int updatedPackagesCount = 0;
            int respondedPackagesCount = 0;
            int unreviewedPackagesCount = 0;
            int waitingPackagesCount = 0;
            var searchFilter = GetSearchFilter(q, sortOrder, page, prerelease);

            if (moderatorQueue)
            {
                var submittedPackages = packageSvc.GetSubmittedPackages().ToList();

                var updatedStatus = PackageSubmittedStatusType.Updated.ToString();
                var respondedStatus = PackageSubmittedStatusType.Responded.ToString();
                var readyStatus = PackageSubmittedStatusType.Ready.ToString();
                var waitingStatus = PackageSubmittedStatusType.Waiting.ToString();

                //var resubmittedPackages = submittedPackages.Where(p => p.ReviewedDate.HasValue && p.Published > p.ReviewedDate).OrderBy(p => p.Published).ToList();
                var resubmittedPackages = submittedPackages.Where(p => p.SubmittedStatusForDatabase == updatedStatus).OrderBy(p => p.Published).ToList();
                updatedPackagesCount = resubmittedPackages.Count;

                var respondedPackages = submittedPackages.Where(p => p.SubmittedStatusForDatabase == respondedStatus).OrderBy(p => p.LastUpdated).ToList();
                respondedPackagesCount = respondedPackages.Count;

                var unreviewedPackages = submittedPackages.Where(p => p.SubmittedStatusForDatabase == readyStatus || p.SubmittedStatusForDatabase == null).OrderBy(p => p.Published).ToList();
                unreviewedPackagesCount = unreviewedPackages.Count;

                //var waitingForMaintainerPackages = submittedPackages.Where(p => p.ReviewedDate >= p.Published).OrderByDescending(p => p.ReviewedDate).ToList();
                var waitingForMaintainerPackages = submittedPackages.Where(p => p.SubmittedStatusForDatabase == waitingStatus).OrderByDescending(p => p.ReviewedDate).ToList();
                waitingPackagesCount = waitingForMaintainerPackages.Count;

                packagesToShow = resubmittedPackages.Union(respondedPackages).Union(unreviewedPackages).Union(waitingForMaintainerPackages);

                if (!string.IsNullOrWhiteSpace(q))
                {
                    packagesToShow = packagesToShow.AsQueryable().Search(q).ToList();
                }

                switch (searchFilter.SortProperty)
                {
                    case SortProperty.DisplayName:
                        packagesToShow = packagesToShow.OrderBy(p => p.Title);
                        break;
                    case SortProperty.Recent:
                        packagesToShow = packagesToShow.OrderByDescending(p => p.Published);
                        break;
                    default:
                        //do not change the search order
                        break;
                }

                totalHits = packagesToShow.Count() + packageVersions.Count();

                if ((searchFilter.Skip + searchFilter.Take) >= packagesToShow.Count() & string.IsNullOrWhiteSpace(q)) packagesToShow = packagesToShow.Union(packageVersions.OrderByDescending(pv => pv.PackageRegistration.DownloadCount));

                packagesToShow = packagesToShow.Skip(searchFilter.Skip).Take(searchFilter.Take);
            }
            else
            {
                packagesToShow = string.IsNullOrWhiteSpace(q) ? packageVersions : packageVersions.AsQueryable().Search(q).ToList();

                totalHits = packagesToShow.Count();

                switch (searchFilter.SortProperty)
                {
                    case SortProperty.DisplayName:
                        packagesToShow = packagesToShow.OrderBy(p => string.IsNullOrWhiteSpace(p.Title) ? p.PackageRegistration.Id : p.Title);
                        break;
                    case SortProperty.DownloadCount:
                        packagesToShow = packagesToShow.OrderByDescending(p => p.PackageRegistration.DownloadCount);
                        break;
                    case SortProperty.Recent:
                        packagesToShow = packagesToShow.OrderByDescending(p => p.Published);
                        break;
                    case SortProperty.Relevance:
                        //todo: address relevance
                        packagesToShow = packagesToShow.OrderByDescending(p => p.PackageRegistration.DownloadCount);
                        break;
                }

                if (searchFilter.Skip >= totalHits)
                {
                    searchFilter.Skip = 0;
                }

                if ((searchFilter.Skip + searchFilter.Take) >= packagesToShow.Count()) searchFilter.Take = packagesToShow.Count() - searchFilter.Skip;

                packagesToShow = packagesToShow.Skip(searchFilter.Skip).Take(searchFilter.Take);

                //packagesToShow = searchSvc.Search(packageVersions.AsQueryable(), searchFilter, out totalHits).ToList();
            }


            if (page == 1 && !packagesToShow.Any())
            {
                // In the event the index wasn't updated, we may get an incorrect count. 
                totalHits = 0;
            }

            var viewModel = new PackageListViewModel(
                packagesToShow, q, sortOrder, totalHits, page - 1, Constants.DefaultPackageListPageSize, Url, prerelease, moderatorQueue, updatedPackagesCount, unreviewedPackagesCount, waitingPackagesCount, respondedPackagesCount);

            ViewBag.SearchTerm = q;

            return View("~/Views/Packages/ListPackages.cshtml", viewModel);
        }

        // NOTE: Intentionally NOT requiring authentication
        public virtual ActionResult ReportAbuse(string id, string version)
        {
            var package = packageSvc.FindPackageByIdAndVersion(id, version);

            if (package == null) return PackageNotFound(id, version);

            var model = new ReportAbuseViewModel
            {
                PackageId = id,
                PackageVersion = package.Version,
            };

            if (Request.IsAuthenticated)
            {
                var user = userSvc.FindByUsername(HttpContext.User.Identity.Name);
                if (user.Confirmed) model.ConfirmedUser = true;
            }

            return View("~/Views/Packages/ReportAbuse.cshtml", model);
        }

        [HttpPost, ValidateAntiForgeryToken, ValidateSpamPrevention]
        public virtual ActionResult ReportAbuse(string id, string version, ReportAbuseViewModel reportForm)
        {
            if (!ModelState.IsValid) return ReportAbuse(id, version);

            var package = packageSvc.FindPackageByIdAndVersion(id, version);
            if (package == null) return PackageNotFound(id, version);

            MailAddress from = null;
            if (Request.IsAuthenticated)
            {
                var user = userSvc.FindByUsername(HttpContext.User.Identity.Name);
                from = user.ToMailAddress();
            }
            else from = new MailAddress(reportForm.Email);

            var packageUrl = EnsureTrailingSlash(Configuration.GetSiteRoot(useHttps: false)) + RemoveStartingSlash(Url.Package(package));

            messageService.ReportAbuse(from, package, reportForm.Message, packageUrl, reportForm.CopySender);

            TempData["Message"] = "Your abuse report has been sent to the site admins.";
            return RedirectToAction(MVC.Packages.DisplayPackage(id, version));
        }

        // NOTE: Intentionally NOT requiring authentication
        public virtual ActionResult ContactAdmins(string id, string version)
        {
            var package = packageSvc.FindPackageByIdAndVersion(id, version);

            if (package == null) return PackageNotFound(id, version);

            var model = new ReportAbuseViewModel
            {
                PackageId = id,
                PackageVersion = package.Version,
            };

            if (Request.IsAuthenticated)
            {
                var user = userSvc.FindByUsername(HttpContext.User.Identity.Name);
                if (user.Confirmed) model.ConfirmedUser = true;
            }

            return View("~/Views/Packages/ContactAdmins.cshtml", model);
        }

        [HttpPost, ValidateAntiForgeryToken, ValidateSpamPrevention]
        public virtual ActionResult ContactAdmins(string id, string version, ReportAbuseViewModel reportForm)
        {
            if (!ModelState.IsValid) return ContactAdmins(id, version);

            var package = packageSvc.FindPackageByIdAndVersion(id, version);
            if (package == null) return PackageNotFound(id, version);

            MailAddress from = null;
            if (Request.IsAuthenticated)
            {
                var user = userSvc.FindByUsername(HttpContext.User.Identity.Name);
                from = user.ToMailAddress();
            }
            else from = new MailAddress(reportForm.Email);

            var packageUrl = EnsureTrailingSlash(Configuration.GetSiteRoot(useHttps: false)) + RemoveStartingSlash(Url.Package(package));

            messageService.ContactSiteAdmins(from, package, reportForm.Message, packageUrl, reportForm.CopySender);

            TempData["Message"] = "Your message has been sent to the site admins.";
            return RedirectToAction(MVC.Packages.DisplayPackage(id, version));
        }

        // NOTE: Intentionally NOT requiring authentication
        public virtual ActionResult ContactOwners(string id)
        {
            var package = packageSvc.FindPackageRegistrationById(id);

            if (package == null) return PackageNotFound(id);

            var model = new ContactOwnersViewModel
            {
                PackageId = package.Id,
                Owners = package.Owners.Where(u => u.EmailAllowed)
            };

            if (Request.IsAuthenticated)
            {
                var user = userSvc.FindByUsername(HttpContext.User.Identity.Name);
                if (user.Confirmed) model.ConfirmedUser = true;
            }

            return View("~/Views/Packages/ContactOwners.cshtml", model);
        }

        [HttpPost, ValidateAntiForgeryToken, ValidateSpamPrevention]
        public virtual ActionResult ContactOwners(string id, ContactOwnersViewModel contactForm)
        {
            if (!ModelState.IsValid) return ContactOwners(id);

            var package = packageSvc.FindPackageRegistrationById(id);
            if (package == null) return PackageNotFound(id);

            MailAddress from = null;
            if (Request.IsAuthenticated)
            {
                var user = userSvc.FindByUsername(HttpContext.User.Identity.Name);
                from = user.ToMailAddress();
            }
            else from = new MailAddress(contactForm.Email);

            var packageUrl = EnsureTrailingSlash(Configuration.GetSiteRoot(useHttps: false)) + RemoveStartingSlash(Url.Package(package));

            messageService.SendContactOwnersMessage(from, package, contactForm.Message, Url.Action(MVC.Users.Edit(), protocol: Request.Url.Scheme), packageUrl, contactForm.CopySender);

            string message = String.Format(CultureInfo.CurrentCulture, "Your message has been sent to the maintainers of {0}.", id);
            TempData["Message"] = message;
            return RedirectToAction(MVC.Packages.DisplayPackage(id, null));
        }

        // This is the page that explains why there's no download link.
        public virtual ActionResult Download()
        {
            return View("~/Views/Packages/Download.cshtml");
        }

        private bool UserHasPackageChangePermissions(IPrincipal user, Package package)
        {
            if (user != null && (package.IsOwner(user) || user.IsModerator())) return true;

            return false;
        }

        [Authorize]
        public virtual ActionResult ManagePackageOwners(string id, string version)
        {
            var package = packageSvc.FindPackageByIdAndVersion(id, version);
            if (package == null) return PackageNotFound(id, version);

            if (!UserHasPackageChangePermissions(HttpContext.User, package)) return new HttpStatusCodeResult(401, "Unauthorized");

            var model = new ManagePackageOwnersViewModel(package, HttpContext.User);

            return View("~/Views/Packages/ManagePackageOwners.cshtml", model);
        }

        [Authorize]
        public virtual ActionResult Delete(string id, string version)
        {
            return GetPackageOwnerActionFormResult(id, version);
        }

        [Authorize, HttpPost, ValidateAntiForgeryToken]
        public virtual ActionResult Delete(string id, string version, bool? listed)
        {
            return Delete(id, version, listed, Url.Package);
        }

        internal virtual ActionResult Delete(string id, string version, bool? listed, Func<Package, string> urlFactory)
        {
            var package = packageSvc.FindPackageByIdAndVersion(id, version, allowPrerelease: true, useCache: false);
            if (package == null) return PackageNotFound(id, version);
            if (!UserHasPackageChangePermissions(HttpContext.User, package)) return new HttpStatusCodeResult(401, "Unauthorized");

            if (!(listed ?? false)) packageSvc.MarkPackageUnlisted(package);
            else packageSvc.MarkPackageListed(package);

            return Redirect(urlFactory(package));
        }

        [Authorize]
        public virtual ActionResult Edit(string id, string version)
        {
            return GetPackageOwnerActionFormResult(id, version);
        }

        [Authorize, HttpPost, ValidateAntiForgeryToken]
        public virtual ActionResult Edit(string id, string version, bool? listed)
        {
            return Edit(id, version, listed, Url.Package);
        }

        [Authorize]
        public virtual ActionResult ConfirmOwner(string id, string username, string token)
        {
            if (String.IsNullOrEmpty(token)) return HttpNotFound();

            var package = packageSvc.FindPackageRegistrationById(id, useCache: false);
            if (package == null) return HttpNotFound();

            var user = userSvc.FindByUsername(username);
            if (user == null) return HttpNotFound();

            if (!String.Equals(user.Username, User.Identity.Name, StringComparison.OrdinalIgnoreCase)) return new HttpStatusCodeResult(403);

            var model = new PackageOwnerConfirmationModel
            {
                Success = packageSvc.ConfirmPackageOwner(package, user, token),
                PackageId = id
            };

            return View("~/Views/Packages/ConfirmOwner.cshtml", model);
        }

        internal virtual ActionResult Edit(string id, string version, bool? listed, Func<Package, string> urlFactory)
        {
            var package = packageSvc.FindPackageByIdAndVersion(id, version, allowPrerelease: true, useCache: false);
            if (package == null) return PackageNotFound(id, version);

            if (!UserHasPackageChangePermissions(HttpContext.User, package)) return new HttpStatusCodeResult(401, "Unauthorized");

            if (!(listed ?? false)) packageSvc.MarkPackageUnlisted(package);
            else packageSvc.MarkPackageListed(package);
            return Redirect(urlFactory(package));
        }

        private ActionResult GetPackageOwnerActionFormResult(string id, string version)
        {
            var package = packageSvc.FindPackageByIdAndVersion(id, version);
            if (package == null) return PackageNotFound(id, version);
            if (!UserHasPackageChangePermissions(HttpContext.User, package)) return new HttpStatusCodeResult(401, "Unauthorized");

            var model = new DisplayPackageViewModel(package);
            return View(model);
        }

        // We may want to have a specific behavior for package not found
        private ActionResult PackageNotFound(string id)
        {
            return PackageNotFound(id, null);
        }

        private ActionResult PackageNotFound(string id, string version)
        {
            return HttpNotFound();
        }

        [Authorize]
        public virtual ActionResult VerifyPackage()
        {
            var currentUser = userSvc.FindByUsername(GetIdentity().Name);

            IPackage package;
            using (var uploadFile = uploadFileSvc.GetUploadFile(currentUser.Key))
            {
                if (uploadFile == null)
                {
                    TempData["ErrorMessage"] = string.Format("Unable to find uploaded file for user '{0}'. Please try using choco.exe push instead.", currentUser.Username);
                    return RedirectToRoute(RouteName.UploadPackage);
                }

                package = ReadNuGetPackage(uploadFile);
            }

            return View(
                "~/Views/Packages/VerifyPackage.cshtml", new VerifyPackageViewModel
                {
                    Id = package.Id,
                    Version = package.Version.ToStringSafe(),
                    Title = package.Title,
                    Summary = package.Summary,
                    Description = package.Description,
                    RequiresLicenseAcceptance = package.RequireLicenseAcceptance,
                    LicenseUrl = package.LicenseUrl.ToStringSafe(),
                    Tags = package.Tags,
                    ProjectUrl = package.ProjectUrl.ToStringSafe(),
                    Authors = package.Authors.Flatten(),
                    Listed = package.Listed
                });
        }

        [Authorize, HttpPost, ValidateAntiForgeryToken]
        public virtual ActionResult VerifyPackage(bool? listed)
        {
            var currentUser = userSvc.FindByUsername(GetIdentity().Name);

            IPackage nugetPackage;
            using (var uploadFile = uploadFileSvc.GetUploadFile(currentUser.Key))
            {
                if (uploadFile == null) return HttpNotFound();
                nugetPackage = ReadNuGetPackage(uploadFile);
            }

            Package package;
            using (var tx = new TransactionScope())
            {
                package = packageSvc.CreatePackage(nugetPackage, currentUser);
                packageSvc.PublishPackage(package.PackageRegistration.Id, package.Version);
                if (listed.HasValue && listed.Value == false) packageSvc.MarkPackageUnlisted(package);
                uploadFileSvc.DeleteUploadFile(currentUser.Key);
                autoCuratedPackageCmd.Execute(package, nugetPackage);
                tx.Complete();
            }

            TempData["Message"] = string.Format(
                "You have successfully created '{0}' version '{1}'. The package is now under review by the moderators and will show up once approved.", package.PackageRegistration.Id, package.Version);
            return RedirectToRoute(
                RouteName.DisplayPackage, new
                {
                    package.PackageRegistration.Id,
                    package.Version
                });
        }

        [Authorize, HttpPost, ValidateAntiForgeryToken]
        public virtual ActionResult CancelUpload()
        {
            var currentUser = userSvc.FindByUsername(GetIdentity().Name);
            uploadFileSvc.DeleteUploadFile(currentUser.Key);

            return RedirectToAction(MVC.Packages.UploadPackage());
        }

        [HttpPost]
        public virtual ActionResult NotifyMaintainersOfAddedComment(string packageId, CommentViewModel commentViewModel)
        {
            var package = packageSvc.FindPackageRegistrationById(packageId);
            if (package == null) return PackageNotFound(packageId);

            var packageUrl = EnsureTrailingSlash(Configuration.GetSiteRoot(useHttps: false)) + RemoveStartingSlash(Url.Package(package));

            messageService.SendCommentNotificationToMaintainers(package, commentViewModel, packageUrl);

            return new HttpStatusCodeResult(200);
        }

        // this methods exist to make unit testing easier
        protected internal virtual IIdentity GetIdentity()
        {
            return User.Identity;
        }

        // this methods exist to make unit testing easier
        protected internal virtual IPackage ReadNuGetPackage(Stream stream)
        {
            return new ZipPackage(stream);
        }

        private SearchFilter GetSearchFilter(string q, string sortOrder, int page, bool includePrerelease)
        {
            var searchFilter = new SearchFilter
            {
                SearchTerm = q,
                Skip = (page - 1) * Constants.DefaultPackageListPageSize, // pages are 1-based. 
                Take = Constants.DefaultPackageListPageSize,
                IncludePrerelease = includePrerelease
            };

            switch (sortOrder)
            {
                case Constants.AlphabeticSortOrder:
                    searchFilter.SortProperty = SortProperty.DisplayName;
                    searchFilter.SortDirection = SortDirection.Ascending;
                    break;
                case Constants.RecentSortOrder:
                    searchFilter.SortProperty = SortProperty.Recent;
                    break;
                case Constants.PopularitySortOrder:
                    searchFilter.SortProperty = SortProperty.DownloadCount;
                    break;
                default:
                    searchFilter.SortProperty = SortProperty.Relevance;
                    break;
            }
            return searchFilter;
        }

        private static string GetSortExpression(string sortOrder)
        {
            switch (sortOrder)
            {
                case Constants.AlphabeticSortOrder:
                    return "PackageRegistration.Id";
                case Constants.RecentSortOrder:
                    return "Published desc";
                case Constants.PopularitySortOrder:
                default:
                    return "PackageRegistration.DownloadCount desc";
            }
        }

        private static string EnsureTrailingSlash(string siteRoot)
        {
            if (!siteRoot.EndsWith("/", StringComparison.Ordinal)) siteRoot = siteRoot + '/';
            return siteRoot;
        }

        private static string RemoveStartingSlash(string urlPath)
        {
            if (urlPath.StartsWith("/")) return urlPath.Substring(1);

            return urlPath;
        }
    }
}
