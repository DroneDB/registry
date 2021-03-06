﻿using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers
{
    public class LocalLoginManager : ILoginManager
    {
        private readonly UserManager<User> _userManager;
        private readonly SignInManager<User> _signInManager;
        private readonly ILogger<ILoginManager> _logger;
        private readonly AppSettings _settings;

        public LocalLoginManager(UserManager<User> userManager,
            SignInManager<User> signInManager,
            ILogger<ILoginManager> logger,
            IOptions<AppSettings> settings)
        {
            _userManager = userManager;
            _logger = logger;
            _signInManager = signInManager;
            _settings = settings.Value;
        }

        public Task<LoginResult> CheckAccess(string token)
        {
            // No token login for local auth
            return Task.FromResult(new LoginResult
            {
                Success = false
            });
        }

        // This is basically a stub
        public async Task<LoginResult> CheckAccess(string userName, string password)
        {
            var user = await _userManager.FindByNameAsync(userName);

            if (user == null)
                return new LoginResult
                {
                    Success = false,
                    UserName = userName
                };
            
            var res = await _signInManager.CheckPasswordSignInAsync(user, password, false);

            return new LoginResult
            {
                Success = res.Succeeded,
                UserName = userName,
                Metadata = user.Metadata
            };
        }

    }
}