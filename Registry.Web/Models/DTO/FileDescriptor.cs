﻿using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MimeMapping;
using Registry.Common;
using Registry.Ports.ObjectSystem;
using Registry.Web.Exceptions;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Models.DTO
{
    public class FileDescriptor
    {
        private readonly string _orgSlug;
        private readonly Guid _internalRef;
        private readonly string[] _paths;
        private readonly bool _isSingle;
        private readonly IObjectSystem _objectSystem;
        private readonly IObjectsManager _objectManager;
        private readonly ILogger<ObjectsManager> _logger;

        public string Name { get; }
        
        public string ContentType { get; }

        public FileDescriptor(string name, string contentType, string orgSlug, Guid internalRef, string[] paths,
            bool isSingle, IObjectSystem objectSystem, IObjectsManager objectManager, ILogger<ObjectsManager> logger)
        {
            _orgSlug = orgSlug;
            _internalRef = internalRef;
            _paths = paths;
            _isSingle = isSingle;
            _objectSystem = objectSystem;
            _objectManager = objectManager;
            _logger = logger;
            Name = name;
            ContentType = contentType;
        }

        public async Task CopyToAsync(Stream stream)
        {
            // If there is just one file we return it
            if (_isSingle)
            {
                var filePath = _paths.First();

                _logger.LogInformation($"Only one path found: '{filePath}'");
                
                await WriteObjectContentStream(_orgSlug, _internalRef, filePath, stream);

            }
            // Otherwise we zip everything together and return the package
            else
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, true);
                foreach (var path in _paths)
                {
                    _logger.LogInformation($"Zipping: '{path}'");

                    var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();

                    await WriteObjectContentStream(_orgSlug, _internalRef, path, entryStream);
                }
            }

        }

        private async Task WriteObjectContentStream(string orgSlug, Guid internalRef, string path, Stream stream)
        {
            var bucketName = _objectManager.GetBucketName(orgSlug, internalRef);

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
    }
}