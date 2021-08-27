﻿using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using SuccessRecruitment.DataTransferObjects.Auth;
using SuccessRecruitment.Models;

namespace SuccessRecruitment.Services.Auth
{
    public interface IAuthService
    {
        Task<ValidUserDTO> Register(UserRegisterDTO newUser);
        Task<ValidUserDTO> Login(UserLoginDTO user);
    }

    public class AuthService : IAuthService
    {
        private readonly RecruitmentDB _db;
        private readonly SymmetricSecurityKey _key;

        public AuthService(RecruitmentDB database, IConfiguration config)
        { 
            _db = database;
            _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["TokenKey"]));
        }

        public async Task<ValidUserDTO> Login(UserLoginDTO user)
        {
            try
            {
                ValidUserDTO validUser = new ValidUserDTO();
                validUser.UserDetails = await _db.Tblusers.Include(x => x.TblLogin).Include(x=> x.TblUserRoles).Include(x=> x.TblUserPages).Where(u => u.UserName == user.UserName).FirstOrDefaultAsync();
                
                if (validUser.UserDetails == null)
                {
                    throw new Exception("Incorrect Username or Password");
                }


                HMACSHA512 hmac = new HMACSHA512(validUser.UserDetails.TblLogin.PasswordSalt);

                var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(user.Password));

                for (var i = 0; i < computedHash.Length; i++)
                {
                    if (computedHash[i] != validUser.UserDetails.TblLogin.PasswordHash[i])
                    {
                        throw new Exception("Incorrect Username or Password");
                    }
                }

                List<int> roleIds = validUser.UserDetails.TblUserRoles.Select(x => x.RoleId).ToList();
                List<int> pageIds = validUser.UserDetails.TblUserPages.Select(x => x.PageId).ToList();
                List<string> userRoles = await _db.TblUserRoles.Include(x => x.TblRole).Where(x => roleIds.Contains(x.RoleId) && !x.IsArchived).Select(x => x.TblRole.RoleName).ToListAsync();
                List<string> userPages = await _db.TblUserPages.Include(x => x.TblPage).Where(x => pageIds.Contains(x.PageId) && !x.IsArchived).Select(x => x.TblPage.PageName).ToListAsync();
                //try to get all these results in 1 query
                validUser.Token = CreateToken(validUser.UserDetails, userRoles, userPages);

                if (validUser.Token == null)
                {
                    throw new Exception("Unable to create Token. Please contact system administrator");
                }
               
                return validUser;
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }
          
        }

        public async Task<ValidUserDTO> Register(UserRegisterDTO newUser)
        {
            try
            {
                ValidUserDTO validUser = new ValidUserDTO();
                
                if(newUser.UserName == null || newUser.Email == null || newUser.Password == null || newUser.RoleIds.Count == 0)
                {
                    throw new Exception("User details and role are required");
                }

                List<TblRole> roles = await _db.TblRoles.Where(x => newUser.RoleIds.Contains(x.RoleId)).ToListAsync();
                List<TblPage> pages = new List<TblPage>();
                if (roles.Any(x=> x.IsArchived))
                {
                    throw new Exception("One or more selected roles have been archived. Please contact system administrator");
                }

                bool userExists = await _db.Tblusers.AnyAsync(x => x.UserName == newUser.UserName.Trim() && x.Email == newUser.Email.Trim() && !x.IsArchived);

                if (userExists)
                {
                    throw new Exception("User Already exists. Please Log in");
                }
                else
                {
                    validUser.UserDetails = (await _db.Tblusers.AddAsync(new Tbluser
                    {
                        UserId = Guid.NewGuid(),
                        UserName = newUser.UserName,
                        Email = newUser.Email,
                        Phone = newUser.Phone,
                        CreatedBy = Guid.Parse("3AF1BA96-4A39-467C-8AD9-3F418F199CD0"),
                        CreatedDate = DateTime.Now
                    })).Entity;

                    HMACSHA512 hmac = new HMACSHA512();
                    //If a column has been added an identity, the application has to be scafolled otherwise the application will pass a default value from the dataype
                    _db.TblLogins.Add(new TblLogin
                    {
                        UserId = validUser.UserDetails.UserId,
                        PasswordHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(newUser.Password)),
                        PasswordSalt = hmac.Key,
                        CreatedBy = Guid.Parse("3AF1BA96-4A39-467C-8AD9-3F418F199CD0"),
                        CreatedDate = DateTime.Now
                    });

                    List<int> roleIds = roles.Select(x => x.RoleId).ToList();
                    
                    foreach (var roleId in roleIds)
                    {
                        validUser.UserDetails.TblUserRoles.Add((await _db.TblUserRoles.AddAsync(new TblUserRole
                        {
                            UserId = validUser.UserDetails.UserId,
                            RoleId = roleId,
                            CreatedBy = Guid.Parse("3AF1BA96-4A39-467C-8AD9-3F418F199CD0"),
                            CreatedDate = DateTime.Now
                        })).Entity);
                    }

                    bool IsExternal = roles.Any(x => x.RoleName == "Recruiter" || x.RoleName == "Candidate");
                    bool hasAddEditprivileges = roles.Any(x => x.RoleName == "Admin" || x.RoleName == "General Manager");
                    //By default a new user is given access to read only pages unless their role is Admin or General Manager
                    pages = await _db.TblRolePages.Include(x=> x.TblPage).Where(x => roleIds.Contains(x.RoleId) && x.TblPage.IsExternal == IsExternal && !x.IsArchived && !x.TblPage.IsArchived && ( !x.TblPage.IsAddEditPage || hasAddEditprivileges)).Select(x=> x.TblPage).ToListAsync();

                    List<int> pageIds = pages.Select(x => x.PageId).ToList();

                    foreach (var pageId in pageIds)
                    {
                        validUser.UserDetails.TblUserPages.Add((await _db.TblUserPages.AddAsync(new TblUserPage
                        {
                            UserId = validUser.UserDetails.UserId,
                            PageId = pageId,
                            CreatedBy = Guid.Parse("3AF1BA96-4A39-467C-8AD9-3F418F199CD0"),
                            CreatedDate = DateTime.Now
                        })).Entity);
                    }
                }

                validUser.Token = CreateToken(validUser.UserDetails, roles.Select(x => x.RoleName).ToList(), pages.Select(x=> x.PageName).ToList());

                await _db.SaveChangesAsync();

                return validUser;
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }
         
        }

        private string CreateToken(Tbluser user, List<string> roleNames, List<string> pageNames)
        {
            try
            {
                List<Claim> claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString())
            };
                foreach (var roleName in roleNames)
                {
                    claims.Add(new Claim(ClaimTypes.Role, roleName));
                }
                foreach (var pageName in pageNames)
                {
                    claims.Add(new Claim("Pages", pageName));
                }
                
                SigningCredentials creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha512Signature);

                SecurityTokenDescriptor tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(claims),
                    Expires = DateTime.Now.AddDays(1),
                    SigningCredentials = creds
                };

                JwtSecurityTokenHandler tokenHandler = new JwtSecurityTokenHandler();
                SecurityToken token = tokenHandler.CreateToken(tokenDescriptor);

                return tokenHandler.WriteToken(token);
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }

        }
    }
}
