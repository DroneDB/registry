﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web
{
    public static class Extenders
    {
        public static Organization ToEntity(this OrganizationDto organization)
        {
            return new Organization
            {
                Slug = organization.Slug,
                Name = string.IsNullOrEmpty(organization.Name) ? organization.Slug : organization.Name,
                Description = organization.Description,
                CreationDate = organization.CreationDate,
                OwnerId = organization.Owner,
                IsPublic = organization.IsPublic
            };
        }

        public static OrganizationDto ToDto(this Organization organization)
        {
            return new OrganizationDto
            {
                Slug = organization.Slug,
                Name = organization.Name,
                Description = organization.Description,
                CreationDate = organization.CreationDate,
                Owner = organization.OwnerId,
                IsPublic = organization.IsPublic
            };
        }

        public static Dataset ToEntity(this DatasetDto dataset)
        {
            return new Dataset
            {
                Id = dataset.Id,
                Slug = dataset.Slug,
                CreationDate = dataset.CreationDate,
                Description = dataset.Description,
                LastEdit = dataset.LastEdit,
                Name = string.IsNullOrEmpty(dataset.Name) ? dataset.Slug : dataset.Name,
                License = dataset.License,
                Meta = dataset.Meta,
                ObjectsCount = dataset.ObjectsCount,
                Size = dataset.Size,
                IsPublic = dataset.IsPublic
            };
        }

        public static DatasetDto ToDto(this Dataset dataset)
        {
            return new DatasetDto
            {
                Id = dataset.Id,
                Slug = dataset.Slug,
                CreationDate = dataset.CreationDate,
                Description = dataset.Description,
                LastEdit = dataset.LastEdit,
                Name = dataset.Name,
                License = dataset.License,
                Meta = dataset.Meta,
                ObjectsCount = dataset.ObjectsCount,
                Size = dataset.Size
            };
        }

        public static ObjectDto ToDto(this DdbEntry obj)
        {
            return new ObjectDto
            {
                Depth = obj.Depth,
                Hash = obj.Hash,
                Id = obj.Id,
                Meta = obj.Meta,
                ModifiedTime = obj.ModifiedTime,
                Path = obj.Path,
                PointGeometry = obj.PointGeometry,
                PolygonGeometry = obj.PolygonGeometry,
                Size = obj.Size,
                Type = obj.Type
            };
        }

        public static void UpdateStatistics(this Dataset ds, IDdbStorage ddb)
        {
            var objs = ddb.Search(null).ToArray();

            ds.ObjectsCount = objs.Length;
            ds.Size = objs.Sum(item => item.Size);
            
        }
    }
}