namespace NuGetGallery
{
    using System;
    using System.IO;
    using System.IO.Packaging;
    using System.Linq;
    using NuGet;
    using ZipPackage = NuGet.ZipPackage;

    public class ChocolateyPackage : ZipPackage, IPackage
    {

        // We don't store the stream itself, just a way to open the stream on demand
        // so we don't have to hold on to that resource
        private readonly Func<Stream> _streamFactory;
        internal const string ManifestRelationType = "manifest"; 
        internal const string PackageRelationshipNamespace = "http://schemas.microsoft.com/packaging/2010/07/";


        public ChocolateyPackage(string filePath) : base(filePath)
        {
        }

        public ChocolateyPackage(Stream stream) : base(stream)
        {
            _streamFactory = stream.ToStreamFactory();
            EnsureChocolateyManifest();
        }

        private void EnsureChocolateyManifest()
        {
            using (Stream stream = _streamFactory())
            {
                System.IO.Packaging.Package package = System.IO.Packaging.Package.Open(stream);

                PackageRelationship relationshipType = package.GetRelationshipsByType(PackageRelationshipNamespace + ManifestRelationType).SingleOrDefault();
                PackagePart manifestPart = package.GetPart(relationshipType.TargetUri);

                using (Stream manifestStream = manifestPart.GetStream())
                {
                    ReadChocolateyManifest(manifestStream);
                }
            }
        }

        protected void ReadChocolateyManifest(Stream manifestStream)
        {
            Manifest manifest = Manifest.ReadFrom(manifestStream);

            var metadata = manifest.Metadata as IPackageMetadata;

            if (metadata != null)
            {
                PackageRepositoryUrl = metadata.PackageRepositoryUrl;
            }
        }


       public Uri PackageRepositoryUrl { get; private set; }

    }

    public interface IPackage : NuGet.IPackage, IPackageMetadata
    {
    }

    public interface IPackageMetadata : NuGet.IPackageMetadata
    {
        Uri PackageRepositoryUrl { get; }
    }
}