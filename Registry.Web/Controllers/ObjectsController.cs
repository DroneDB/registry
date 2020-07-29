﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNet.OData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Controllers
{

    [Authorize]
    [ApiController]
    [Route("ddb/{orgId:alpha}/ds/{dsId:alpha}/obj")]
    public class ObjectsController : ControllerBaseEx
    {
        private readonly IObjectsController _objectsController;

        public ObjectsController(IObjectsController datasetsManager)
        {
            _objectsController = datasetsManager;
        }
        
        [HttpGet("{**path}", Name = nameof(ObjectsController) + "." + nameof(Get))]
        public async Task<IActionResult> Get([FromRoute] string orgId, [FromRoute] string dsId, string path)
        {
            try
            {
                Debug.WriteLine(orgId);
                return Ok();
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }
        /*

        [HttpGet("{id}", Name = nameof(ObjectsController) + "." + nameof(Get))]
        public async Task<IActionResult> Get([FromRoute] string orgId, string id)
        {
            try
            {
                return Ok(await _objectsController.Get(orgId, id));
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }


        [HttpPost]
        public async Task<IActionResult> Post([FromRoute] string orgId, [FromBody] DatasetDto dataset)
        {
            try
            {
                var newDs = await _objectsController.AddNew(orgId, dataset);
                return CreatedAtRoute(nameof(DatasetsController) + "." + nameof(Get), new { id = newDs.Id },
                    newDs);
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }
        }

        // POST: ddb/
        [HttpPut("{id}")]
        public async Task<IActionResult> Put([FromRoute] string orgId, string id, [FromBody] DatasetDto dataset)
        {

            try
            {
                await _objectsController.Edit(orgId, id, dataset);
                return NoContent();
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }

        }

        // DELETE: ddb/id
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete([FromRoute] string orgId, string id)
        {

            try
            {
                await _objectsController.Delete(orgId, id);
                return NoContent();
            }
            catch (Exception ex)
            {
                return ExceptionResult(ex);
            }

        }

        */
    }
}
