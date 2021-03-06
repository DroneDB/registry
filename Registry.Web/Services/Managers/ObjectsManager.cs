﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DDB.Bindings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeMapping;
using Registry.Adapters;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers
{
    public class ObjectsManager : IObjectsManager
    {
        private readonly ILogger<ObjectsManager> _logger;
        private readonly IObjectSystem _objectSystem;
        private readonly IDdbManager _ddbManager;
        private readonly IUtils _utils;
        private readonly IAuthManager _authManager;
        private readonly ICacheManager _cacheManager;
        private readonly RegistryContext _context;
        private readonly AppSettings _settings;

        // TODO: Could be moved to config
        private const int DefaultThumbnailSize = 512;

        private const string BucketNameFormat = "{0}-{1}";
        private const string TempFolderName = "Registry-ObjectsManager";

        public ObjectsManager(ILogger<ObjectsManager> logger,
            RegistryContext context,
            IObjectSystem objectSystem,
            IOptions<AppSettings> settings,
            IDdbManager ddbManager,
            IUtils utils, IAuthManager authManager, ICacheManager cacheManager)
        {
            _logger = logger;
            _context = context;
            _objectSystem = objectSystem;
            _ddbManager = ddbManager;
            _utils = utils;
            _authManager = authManager;
            _cacheManager = cacheManager;
            _settings = settings.Value;
        }

        public async Task<IEnumerable<ObjectDto>> List(string orgSlug, string dsSlug, string path = null, bool recursive = false)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Searching in '{path}'");

            var files = ddb.Search(path, recursive).Select(file => file.ToDto()).ToArray();

            _logger.LogInformation($"Found {files.Length} objects");

            return files;
        }

        public async Task<ObjectRes> Get(string orgSlug, string dsSlug, string path)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path should not be null");

            return await InternalGet(orgSlug, ds.InternalRef, path);
        }

        private async Task SafeGetFile(string orgSlug, Guid internalRef, string path, string destFile, TimeSpan? maxWaitTime = null)
        {
            var bucketName = GetBucketName(orgSlug, internalRef);

            maxWaitTime ??= new TimeSpan(0, 0, 1, 0);

            if (!File.Exists(destFile))
            {
                // ReSharper disable once MethodHasAsyncOverload
                File.WriteAllText(destFile + ".lock", Environment.TickCount.ToString());

                await using var file = File.OpenWrite(destFile);
                await _objectSystem.GetObjectAsync(bucketName, path, stream => stream.CopyTo(file));

                File.Delete(destFile + ".lock");
            }
            else
            {
                var time = DateTime.Now;
                while (File.Exists(destFile + ".lock"))
                {
                    Thread.Sleep(50);

                    if (DateTime.Now > time + maxWaitTime.Value)
                        throw new InvalidOperationException("Wait time expired, cannot get file");
                }

            }

        }

        private async Task<ObjectRes> InternalGet(string orgSlug, Guid internalRef, string path)
        {
            var ddb = _ddbManager.Get(orgSlug, internalRef);

            if (!ddb.EntryExists(path))
                throw new NotFoundException($"Cannot find '{path}'");

            var bucketName = GetBucketName(orgSlug, internalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            await EnsureBucketExists(bucketName);

            var res = ddb.GetEntry(path);

            if (res.Type == EntryType.Directory)
                throw new InvalidOperationException("Cannot get a folder, we are supposed to deal with a file!");

            var objInfo = await _objectSystem.GetObjectInfoAsync(bucketName, res.Path);

            if (objInfo == null)
                throw new NotFoundException($"Cannot find '{res.Path}' in storage provider");

            await using var memory = new MemoryStream();

            _logger.LogInformation($"Getting object '{res.Path}' in bucket '{bucketName}'");

            await _objectSystem.GetObjectAsync(bucketName, res.Path, stream => stream.CopyTo(memory));

            return new ObjectRes
            {
                ContentType = objInfo.ContentType,
                Name = objInfo.ObjectName,
                Data = memory.ToArray(),
                // TODO: We can add more fields from DDB if we need them
                Type = res.Type,
                Hash = res.Hash,
                Size = res.Size
            };
        }

        public async Task<ObjectDto> AddNew(string orgSlug, string dsSlug, string path, byte[] data)
        {
            await using var stream = new MemoryStream(data);
            stream.Reset();
            return await AddNew(orgSlug, dsSlug, path, stream);
        }

        public async Task<ObjectDto> AddNew(string orgSlug, string dsSlug, string path, Stream stream = null)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            var bucketName = GetBucketName(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            // If the bucket does not exist, let's create it
            await EnsureBucketExists(bucketName);

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            
            // If it's a folder
            if (stream == null)
            {
                if (ddb.EntryExists(path))
                    throw new InvalidOperationException("Cannot create a folder on another entry");

                _logger.LogInformation("Adding folder to DDB");

                // Add to DDB
                ddb.Add(path);

                _logger.LogInformation("Added to DDB");

                return new ObjectDto
                {
                    Path = path,
                    Type = EntryType.Directory,
                    Size = 0
                };
            }

            // TODO: I highly doubt the robustness of this 
            var contentType = MimeTypes.GetMimeType(path);

            var tempFileName = Path.GetTempFileName();

            // Write down the file
            await using (var tempFileStream = File.OpenWrite(tempFileName))
                await stream.CopyToAsync(tempFileStream);
            
            _logger.LogInformation("File uploaded, adding to DDB");

            // Add to DDB
            await using (var tempFileStream = File.OpenRead(tempFileName))
                ddb.Add(path, tempFileStream);
            
            _logger.LogInformation("Added to DDB");

            // We perform the actual upload asynchronously
#pragma warning disable 4014
            Task.Run(async () =>
#pragma warning restore 4014
            {

                _logger.LogInformation($"Uploading '{path}' (size {stream.Length}) to bucket '{bucketName}'");

                await using (var tmpStream = File.OpenRead(tempFileName))
                    await _objectSystem.PutObjectAsync(bucketName, path, tmpStream, tmpStream.Length, contentType);

                _logger.LogInformation($"Deleting temp file '{tempFileName}'");

                if (File.Exists(tempFileName))
                    CommonUtils.SafeDelete(tempFileName);

            });

            var obj = ddb.Search(path).Select(file => file.ToDto()).FirstOrDefault();

            if (obj == null)
                throw new InvalidOperationException("Cannot find just added file!");

            return obj;
        }

        public async Task Move(string orgSlug, string dsSlug, string source, string dest)
        {
            
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            var bucketName = GetBucketName(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            // If the bucket does not exist, let's create it
            await EnsureBucketExists(bucketName);

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var sourceEntry = ddb.GetEntry(source);

            // Checking if source exists
            if (sourceEntry == null)
                throw new InvalidOperationException("Cannot find source entry: '" + source + "'");

            // Short circuit!
            if (source == dest)
            {
                _logger.LogInformation("Source and dest are the same, nothing to do!");
                return;
            }

            var destEntry = ddb.GetEntry(dest);

            if (destEntry != null)
            {
                if (sourceEntry.Type == EntryType.Directory && destEntry.Type != EntryType.Directory)
                    throw new ArgumentException("Cannot move a folder on a file");

                if (sourceEntry.Type != EntryType.Directory && destEntry.Type == EntryType.Directory)
                    throw new ArgumentException("Cannot move a file on a folder");
            }

            var src = ddb.Search(source).Where(item => item.Type != EntryType.Directory).ToArray();

            switch (src.Length)
            {
                // If it's an empty folder
                case 0:

                    _logger.LogInformation("Moving empty folder, nothing to do in object system");

                    break;
                
                case 1:

                    _logger.LogInformation($"Copying object '{source}' to '{dest}'");
                    await _objectSystem.CopyObjectAsync(bucketName, source, bucketName, dest);

                    _logger.LogInformation("Removing source object");
                    await _objectSystem.RemoveObjectAsync(bucketName, source);

                    break;

                // If it's a folder
                default:

                    _logger.LogInformation($"Moving folder '{source}' to '{dest}'");

                    await _objectSystem.MoveDirectory(bucketName, source, dest);
                    _logger.LogInformation("Move OK");

                    break;
            }

            _logger.LogInformation("Performing ddb move");
            ddb.Move(source, dest);

            if (!ddb.EntryExists(dest))
                throw new InvalidOperationException($"Cannot find destination '{dest}' after move, something wrong with ddb");

            _logger.LogInformation("Move OK");


        }

        private string SafeGetLocation()
        {
            if (_settings.StorageProvider.Type != StorageType.S3) return null;

            var settings = _settings.StorageProvider.Settings.ToObject<S3StorageProviderSettings>();
            if (settings == null)
            {
                _logger.LogWarning("No S3 settings loaded, shouldn't this be a problem?");
                return null;
            }

            if (settings.Region == null)
                _logger.LogWarning("No region specified in storage provider config");

            return settings.Region;
        }

        public async Task Delete(string orgSlug, string dsSlug, string path)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");
            
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            
            if (!ddb.EntryExists(path))
                throw new BadRequestException($"Path '{path}' not found in dataset");

            var bucketName = GetBucketName(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);
            if (!bucketExists)
                throw new BadRequestException($"Cannot find bucket '{bucketName}'");

            _logger.LogInformation("Removing from DDB");
            ddb.Remove(path);

            var objs = ddb.Search(path, true).ToArray();
            
            foreach (var obj in objs.Where(item => item.Type != EntryType.Directory))
            {
                _logger.LogInformation($"Deleting '{obj.Path}'");
                await _objectSystem.RemoveObjectAsync(bucketName, obj.Path);
            }

            _logger.LogInformation("Deletion complete");


        }

        public async Task DeleteAll(string orgSlug, string dsSlug)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In DeleteAll('{orgSlug}/{dsSlug}')");

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            var bucketName = GetBucketName(orgSlug, ds.InternalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
            {
                _logger.LogWarning($"Asked to remove non-existing bucket '{bucketName}'");
            }
            else
            {
                _logger.LogInformation("Deleting bucket");
                await _objectSystem.RemoveBucketAsync(bucketName);
                _logger.LogInformation("Bucket deleted");
            }

            _logger.LogInformation("Removing DDB");

            _ddbManager.Delete(orgSlug, ds.InternalRef);

        }

        public async Task<FileDescriptorDto> GenerateThumbnail(string orgSlug, string dsSlug, string path, int? size, bool recreate = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            var entry = EnsurePathValidity(orgSlug, ds.InternalRef, path, out IDdb ddb);

            var fileName = Path.GetFileName(path);
            if (fileName == null)
                throw new ArgumentException("Path is not valid");

            var destFilePath = Path.Combine(Path.GetTempPath(), "out-" + Path.ChangeExtension(fileName, ".jpg"));
            var sourceFilePath = Path.GetTempFileName();

            try
            {

                await _cacheManager.GenerateThumbnail(ddb, sourceFilePath, entry.Hash, size ?? DefaultThumbnailSize, destFilePath, async () =>
                {
                    var obj = await InternalGet(orgSlug, ds.InternalRef, path);
                    await File.WriteAllBytesAsync(sourceFilePath, obj.Data);
                });

                var memory = new MemoryStream(await File.ReadAllBytesAsync(destFilePath));
                memory.Reset();

                return new FileDescriptorDto
                {
                    ContentStream = memory,
                    ContentType = "image/jpeg",
                    Name = Path.ChangeExtension(fileName, ".jpg")
                };
            }
            finally
            {
                if (File.Exists(destFilePath) && !CommonUtils.SafeDelete(destFilePath))
                    _logger.LogWarning($"Cannot delete dest file '{destFilePath}'");

                if (File.Exists(sourceFilePath) && !CommonUtils.SafeDelete(sourceFilePath))
                    _logger.LogWarning($"Cannot delete source file '{sourceFilePath}'");
            }

        }

        public async Task<FileDescriptorDto> GenerateTile(string orgSlug, string dsSlug, string path, int tz, int tx, int ty, bool retina)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            EnsurePathValidity(orgSlug, ds.InternalRef, path, out var ddb);

            var fileName = Path.GetFileName(path);
            if (fileName == null)
                throw new ArgumentException("Path is not valid");

            var sourceFilePath = Path.Combine(Path.GetTempPath(), nameof(GenerateTile), orgSlug, dsSlug, path);

            var dirName = Path.GetDirectoryName(sourceFilePath);
            if (dirName != null)
                Directory.CreateDirectory(dirName);

            await SafeGetFile(orgSlug, ds.InternalRef, path, sourceFilePath);

            _logger.LogInformation($"Generating tile from '{sourceFilePath}'");
            var destFilePath = ddb.GenerateTile(sourceFilePath, tz, tx, ty, retina, true);

            var memory = new MemoryStream(await File.ReadAllBytesAsync(destFilePath));
            memory.Reset();

            return new FileDescriptorDto
            {
                ContentStream = memory,
                ContentType = "image/png",
                Name = fileName
            };

        }

        #region Downloads
        public async Task<string> GetDownloadPackage(string orgSlug, string dsSlug, string[] paths, DateTime? expiration = null, bool isPublic = false)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            EnsurePathsValidity(orgSlug, ds.InternalRef, paths);

            var currentUser = await _authManager.GetCurrentUser();

            var downloadPackage = new DownloadPackage
            {
                CreationDate = DateTime.Now,
                Dataset = ds,
                ExpirationDate = expiration,
                Paths = paths,
                UserName = currentUser.UserName,
                IsPublic = isPublic
            };

            await _context.DownloadPackages.AddAsync(downloadPackage);
            await _context.SaveChangesAsync();

            return downloadPackage.Id.ToString();
        }

        #region Utils

        private DdbEntry EnsurePathValidity(string orgSlug, Guid internalRef, string path)
        {
            return EnsurePathValidity(orgSlug, internalRef, path, out IDdb ddb);
        }

        private DdbEntry EnsurePathValidity(string orgSlug, Guid internalRef, string path, out IDdb ddb)
        {

            if (path.Contains("*") || path.Contains("?") || string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Wildcards or empty paths are not supported");

            ddb = _ddbManager.Get(orgSlug, internalRef);

            var res = ddb.Search(path)?.ToArray();

            if (res == null || !res.Any())
                throw new ArgumentException($"Invalid path: '{path}'");

            return res.First();
        }

        private void EnsurePathsValidity(string orgSlug, Guid internalRef, string[] paths)
        {
            EnsurePathsValidity(orgSlug, internalRef, paths, out IDdb ddb);
        }

        private void EnsurePathsValidity(string orgSlug, Guid internalRef, string[] paths, out IDdb ddb)
        {
            ddb = null;

            if (paths == null || !paths.Any())
                // Everything
                return;

            if (paths.Any(path => path.Contains("*") || path.Contains("?") || string.IsNullOrWhiteSpace(path)))
                throw new ArgumentException("Wildcards or empty paths are not supported");

            if (paths.Length != paths.Distinct().Count())
                throw new ArgumentException("Duplicate paths");

            ddb = _ddbManager.Get(orgSlug, internalRef);
            
            foreach (var path in paths)
                if (!ddb.EntryExists(path))
                    throw new ArgumentException($"Invalid path: '{path}'");
            
        }

        #endregion

        public async Task<FileDescriptorDto> DownloadPackage(string orgSlug, string dsSlug, string packageId)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug, checkOwnership: false);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            if (packageId == null)
                throw new ArgumentException("No package id provided");

            if (!Guid.TryParse(packageId, out var packageGuid))
                throw new ArgumentException("Invalid package id: expected guid");

            var package = _context.DownloadPackages.FirstOrDefault(item => item.Id == packageGuid);

            if (package == null)
                throw new ArgumentException($"Cannot find package with id '{packageId}'");

            var user = await _authManager.GetCurrentUser();

            // If we are not logged-in and this is not a public package
            if (user == null && !package.IsPublic)
                throw new UnauthorizedException("Download not allowed");

            // If it has and expiration date
            if (package.ExpirationDate != null)
            {
                // If expired
                if (DateTime.Now > package.ExpirationDate)
                {
                    _context.DownloadPackages.Remove(package);
                    await _context.SaveChangesAsync();

                    throw new ArgumentException("This package is expired");
                }
            }
            // It's a one-time download
            else
            {
                _context.DownloadPackages.Remove(package);
                await _context.SaveChangesAsync();
            }

            return await GetOfflineFileDescriptor(orgSlug, dsSlug, ds.InternalRef, package.Paths);

        }


        public async Task<FileStreamDescriptor> DownloadStream(string orgSlug, string dsSlug, string[] paths)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            EnsurePathsValidity(orgSlug, ds.InternalRef, paths);

            return GetFileStreamDescriptor(orgSlug, dsSlug, ds.InternalRef, paths);
        }

        public async Task<FileDescriptorDto> Download(string orgSlug, string dsSlug, string[] paths)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            EnsurePathsValidity(orgSlug, ds.InternalRef, paths);

            return await GetOfflineFileDescriptor(orgSlug, dsSlug, ds.InternalRef, paths);
        }

        private FileStreamDescriptor GetFileStreamDescriptor(string orgSlug, string dsSlug, Guid internalRef, string[] paths)
        {
            var ddb = _ddbManager.Get(orgSlug, internalRef);

            var (files, folders, includeDdb) = GetFilePaths(paths, ddb);

            FileStreamDescriptor streamDescriptor;

            // If there is just one file we return it
            if (files.Length == 1 && paths?.Length == 1 && files[0] == paths[0])
            {
                var filePath = files.First();

                _logger.LogInformation($"Only one path found: '{filePath}'");

                streamDescriptor = new FileStreamDescriptor(Path.GetFileName(filePath), MimeUtility.GetMimeMapping(filePath),
                    orgSlug, internalRef, files, null, FileDescriptorType.Single, _objectSystem, this, _logger, _ddbManager);

            }
            // Otherwise we zip everything together and return the package
            else
            {
                streamDescriptor = new FileStreamDescriptor($"{orgSlug}-{dsSlug}-{CommonUtils.RandomString(8)}.zip",
                    "application/zip", orgSlug, internalRef, files, folders,
                    includeDdb ? FileDescriptorType.Dataset : FileDescriptorType.Multiple, _objectSystem, this,
                    _logger, _ddbManager);

            }

            return streamDescriptor;
        }

        private async Task<FileDescriptorDto> GetOfflineFileDescriptor(string orgSlug, string dsSlug, Guid internalRef, string[] paths)
        {
            var ddb = _ddbManager.Get(orgSlug, internalRef);

            var (files, folders, includeDdb) = GetFilePaths(paths, ddb);

            FileDescriptorDto descriptor;

            // If there is just one file we return it
            if (files.Length == 1 && paths?.Length == 1 && files[0] == paths[0])
            {
                var filePath = files.First();

                _logger.LogInformation($"Only one path found: '{filePath}'");

                descriptor = new FileDescriptorDto
                {
                    ContentStream = new MemoryStream(),
                    Name = Path.GetFileName(filePath),
                    ContentType = MimeUtility.GetMimeMapping(filePath)
                };

                await WriteObjectContentStream(orgSlug, internalRef, filePath, descriptor.ContentStream);

                descriptor.ContentStream.Reset();
            }
            // Otherwise we zip everything together and return the package
            else
            {
                descriptor = new FileDescriptorDto
                {
                    Name = $"{orgSlug}-{dsSlug}-{CommonUtils.RandomString(8)}.zip",
                    ContentStream = new MemoryStream(),
                    ContentType = "application/zip"
                };

                using (var archive = new ZipArchive(descriptor.ContentStream, ZipArchiveMode.Create, true))
                {
                    foreach (var path in files)
                    {
                        _logger.LogInformation($"Zipping: '{path}'");

                        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                        await using var entryStream = entry.Open();

                        await WriteObjectContentStream(orgSlug, internalRef, path, entryStream);
                    }

                    // We treat folders separately because if they are empty they would not be included in the archive
                    if (folders != null)
                    {
                        foreach (var folder in folders)
                            archive.CreateEntry(folder + "/");
                    }

                    // Include ddb folder
                    if (includeDdb)
                    {
                        archive.CreateEntryFromAny(Path.Combine(ddb.DatabaseFolder, _ddbManager.DdbFolderName));
                    }
                }

                descriptor.ContentStream.Reset();
            }

            return descriptor;
        }

        private async Task WriteObjectContentStream(string orgSlug, Guid internalRef, string path, Stream stream)
        {
            var bucketName = GetBucketName(orgSlug, internalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
                throw new NotFoundException($"Cannot find bucket '{bucketName}'");

            var objInfo = await _objectSystem.GetObjectInfoAsync(bucketName, path);

            if (objInfo == null)
                throw new NotFoundException($"Cannot find '{path}' in storage provider");

            _logger.LogInformation($"Getting object '{path}' in bucket '{bucketName}'");

            await _objectSystem.GetObjectAsync(bucketName, path, s => s.CopyTo(stream));

        }

        private (string[] files, string[] folders, bool includeDdb) GetFilePaths(string[] paths, IDdb ddb)
        {
            string[] files;
            string[] folders;

            var includeDdb = false;

            if (paths != null)
            {
                var tempFiles = new List<string>();
                var tempFolders = new List<string>();

                foreach (var path in paths)
                {
                    var entry = ddb.GetEntry(path);

                    if (entry.Type == EntryType.Directory)
                    {
                        // We are in recursive mode because the paths could contain other folders that we need to expand
                        var items = ddb.Search(path, true)?.ToArray();

                        if (items == null)
                            throw new InvalidOperationException("Ddb is empty, what should I get?");

                        tempFolders.Add(path);
                        tempFiles.AddRange(items.Where(item => item.Type != EntryType.Directory).Select(item => item.Path));
                        tempFolders.AddRange(items.Where(item => item.Type == EntryType.Directory).Select(item => item.Path));
                    }
                    else
                    {
                        tempFiles.Add(path);
                    }
                }

                // Get rid of possible duplicates and sort
                files = tempFiles.Distinct().OrderBy(item => item).ToArray();
                folders = tempFolders.Distinct().OrderBy(item => item).ToArray();
            }
            else
            {
                var entries = ddb.Search(null, true)?.ToArray();

                if (entries == null || !entries.Any())
                    throw new InvalidOperationException("Ddb is empty, what should I get?");

                // Select everything and sort
                files = entries
                    .Where(entry => entry.Type != EntryType.Directory)
                    .Select(entry => entry.Path)
                    .OrderBy(path => path)
                    .ToArray();

                folders = entries
                    .Where(entry => entry.Type == EntryType.Directory)
                    .Select(entry => entry.Path)
                    .OrderBy(path => path)
                    .ToArray();

                // We include the ddb folder only when asked for the entire dataset
                includeDdb = true;
            }

            _logger.LogInformation($"Found {files.Length} paths");
            return (files, folders, includeDdb);
        }

        #endregion

        public string GetBucketName(string orgSlug, Guid internalRef)
        {
            return string.Format(BucketNameFormat, orgSlug, internalRef.ToString()).ToLowerInvariant();
        }

        public async Task<FileDescriptorDto> GetDdb(string orgSlug, string dsSlug)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In '{orgSlug}/{dsSlug}'");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            try
            {
                // We could do this fully in memory BUT it's not a strict requirement by now: ddb folders are not huge (yet)
                ZipFile.CreateFromDirectory(ddb.DatabaseFolder, tempFile, CompressionLevel.Optimal, false);

                await using var s = File.OpenRead(tempFile);
                var memory = new MemoryStream();
                await s.CopyToAsync(memory);
                memory.Reset();

                return new FileDescriptorDto
                {
                    ContentStream = memory,
                    ContentType = "application/zip",
                    Name = $"{orgSlug}-{dsSlug}-ddb.zip"
                };

            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        public async Task EnsureBucketExists(string bucketName)
        {
            if (!await _objectSystem.BucketExistsAsync(bucketName))
            {

                _logger.LogInformation($"Bucket '{bucketName}' does not exist, creating it");

                await _objectSystem.MakeBucketAsync(bucketName, SafeGetLocation());

                _logger.LogInformation("Bucket created");
            }
            else
            {
                _logger.LogInformation($"Bucket '{bucketName}' already exists");
            }
        }


    }
}
