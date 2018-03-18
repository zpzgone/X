﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Web;
using NewLife.Cube.Entity;
using NewLife.Log;
using NewLife.Model;
using NewLife.Reflection;
using NewLife.Security;
using NewLife.Web;
using XCode.Membership;

namespace NewLife.Cube.Web
{
    /// <summary>单点登录提供者</summary>
    public class SsoProvider
    {
        #region 属性
        /// <summary>用户管理提供者</summary>
        public IManageProvider Provider { get; set; }

        /// <summary>重定向地址。~/Sso/LoginInfo</summary>
        public String RedirectUrl { get; set; }

        /// <summary>登录成功后跳转地址。~/Admin</summary>
        public String SuccessUrl { get; set; }

        /// <summary>本地登录检查地址。~/Admin/User/Login</summary>
        public String LoginUrl { get; set; }

        /// <summary>已登录用户</summary>
        public IManageUser Current => Provider.Current;
        #endregion

        #region 构造
        /// <summary>实例化</summary>
        public SsoProvider()
        {
            Provider = ManageProvider.Provider;
            RedirectUrl = "~/Sso/LoginInfo";
            SuccessUrl = "~/Admin";
            LoginUrl = "~/Admin/User/Login";
        }
        #endregion

        #region 方法
        /// <summary>获取OAuth客户端</summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public virtual OAuthClient GetClient(String name) => OAuthClient.Create(name);

        /// <summary>获取返回地址</summary>
        /// <param name="request">请求对象</param>
        /// <param name="referr">是否使用引用</param>
        /// <returns></returns>
        public virtual String GetReturnUrl(HttpRequestBase request, Boolean referr)
        {
            var url = request["r"];
            if (url.IsNullOrEmpty() && referr) url = request.UrlReferrer + "";
            if (!url.IsNullOrEmpty() && url.StartsWithIgnoreCase("http"))
            {
                var baseUri = request.GetRawUrl();

                var uri = new Uri(url);
                if (uri != null && uri.Host.EqualIgnoreCase(baseUri.Host)) url = uri.PathAndQuery;
            }

            return url;
        }

        /// <summary>获取回调地址</summary>
        /// <param name="request"></param>
        /// <param name="returnUrl"></param>
        /// <returns></returns>
        public virtual String GetRedirect(HttpRequestBase request, String returnUrl = null)
        {
            if (returnUrl.IsNullOrEmpty()) returnUrl = request["r"];
            // 过滤环回重定向
            if (!returnUrl.IsNullOrEmpty() && returnUrl.StartsWithIgnoreCase("/Sso/Login")) returnUrl = null;

            var uri = RedirectUrl.AsUri(request.GetRawUrl()) + "";

            return uri.AppendReturn(returnUrl);
        }

        /// <summary>登录成功</summary>
        /// <param name="client">OAuth客户端</param>
        /// <param name="service">服务提供者。可用于获取HttpContext成员</param>
        /// <returns></returns>
        public virtual String OnLogin(OAuthClient client, IServiceProvider service)
        {
            var openid = client.OpenID;
            if (openid.IsNullOrEmpty()) openid = client.UserName;

            // 根据OpenID找到用户绑定信息
            var uc = UserConnect.FindByProviderAndOpenID(client.Name, openid);
            if (uc == null) uc = new UserConnect { Provider = client.Name, OpenID = openid };

            uc.Fill(client);

            // 强行绑定，把第三方账号强行绑定到当前已登录账号
            var forceBind = false;
            var req = service.GetService<HttpRequest>();
            if (req != null) forceBind = req["sso_action"].EqualIgnoreCase("bind");

            // 检查绑定
            var user = Provider.FindByID(uc.UserID);
            if (forceBind || user == null || !uc.Enable) user = OnBind(uc, client);

            // 填充昵称等数据
            Fill(client, user);

            if (user is IAuthUser user3)
            {
                user3.Logins++;
                user3.LastLogin = DateTime.Now;
                user3.LastLoginIP = WebHelper.UserHost;
                user3.Save();
            }
            uc.Save();

            if (!user.Enable) throw new InvalidOperationException("用户已禁用！");

            // 登录成功，保存当前用户
            Provider.Current = user;

            return SuccessUrl;
        }

        /// <summary>填充用户，登录成功并获取用户信息之后</summary>
        /// <param name="client"></param>
        /// <param name="user"></param>
        protected virtual void Fill(OAuthClient client, IManageUser user)
        {
            client.Fill(user);

            var dic = client.Items;
            // 用户信息
            if (dic != null && user is UserX user2)
            {
                if (user2.Mail.IsNullOrEmpty() && dic.TryGetValue("email", out var email)) user2.Mail = email;
                if (user2.Mail.IsNullOrEmpty() && dic.TryGetValue("mail", out email)) user2.Mail = email;
                if (user2.Mobile.IsNullOrEmpty() && dic.TryGetValue("mobile", out var mobile)) user2.Mobile = mobile;
                if (user2.Code.IsNullOrEmpty() && dic.TryGetValue("code", out var code)) user2.Code = code;
                if (user2.Sex == SexKinds.未知 && dic.TryGetValue("sex", out var sex)) user2.Sex = (SexKinds)sex.ToInt();

                // 如果默认角色为0，则使用认证中心提供的角色
                var set = Setting.Current;
                var rid = set.DefaultRole;
                //if (rid == 0 && dic.TryGetValue("roleid", out var roleid) && roleid.ToInt() > 0) user2.RoleID = roleid.ToInt();
                if (rid <= 0)
                {
                    // 0使用认证中心角色，-1强制使用
                    if (user2.RoleID <= 0 || rid < 0) user2.RoleID = GetRole(dic, rid < 0);
                }

                // 头像
                if (user2.Avatar.IsNullOrEmpty()) user2.Avatar = client.Avatar;

                // 下载远程头像到本地，Avatar还是保存远程头像地址
                if (user2.Avatar.StartsWithIgnoreCase("http") && !set.AvatarPath.IsNullOrEmpty()) FetchAvatar(user);
            }
        }

        /// <summary>绑定用户，用户未有效绑定或需要强制绑定时</summary>
        /// <param name="uc"></param>
        /// <param name="client"></param>
        public virtual IManageUser OnBind(UserConnect uc, OAuthClient client)
        {
            var prv = Provider;

            // 如果未登录，需要注册一个
            var user = prv.Current;
            if (user == null)
            {
                var set = Setting.Current;
                if (!set.AutoRegister) throw new InvalidOperationException("绑定要求本地已登录！");

                // 先找用户名，如果存在，就加上提供者前缀，直接覆盖
                var name = client.UserName;
                if (!name.IsNullOrEmpty())
                {
                    user = prv.FindByName(name);
                    if (user != null)
                    {
                        name = client.Name + "_" + name;
                        user = prv.FindByName(name);
                    }
                }
                else
                // QQ、微信 等不返回用户名
                {
                    // OpenID和AccessToken不可能同时为空
                    var openid = client.OpenID;
                    if (openid.IsNullOrEmpty()) openid = client.AccessToken;

                    // 过长，需要随机一个较短的
                    var num = openid.GetBytes().Crc();

                    name = client.Name + "_" + num.ToString("X8");
                    user = prv.FindByName(name);
                }

                if (user == null)
                {
                    // 新注册用户采用魔方默认角色
                    var rid = set.DefaultRole;
                    //if (rid == 0 && client.Items.TryGetValue("roleid", out var roleid)) rid = roleid.ToInt();
                    if (rid <= 0) rid = GetRole(client.Items, rid < 0);

                    // 注册用户，随机密码
                    user = prv.Register(name, Rand.NextString(16), rid, true);
                }
            }

            uc.UserID = user.ID;
            uc.Enable = true;

            return user;
        }

        /// <summary>注销</summary>
        /// <returns></returns>
        public virtual void Logout()
        {
            Provider.Current = null;
        }
        #endregion

        #region 服务端
        /// <summary>获取访问令牌</summary>
        /// <param name="sso"></param>
        /// <param name="code"></param>
        /// <returns></returns>
        public virtual Object GetAccessToken(OAuthServer sso, String code)
        {
            var token = sso.GetToken(code);

            return new
            {
                access_token = token,
                expires_in = sso.Expire,
                scope = "basic,UserInfo",
            };
        }

        /// <summary>获取用户信息</summary>
        /// <param name="sso"></param>
        /// <param name="token"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        public virtual Object GetUserInfo(OAuthServer sso, String token, IManageUser user)
        {
            if (user is UserX user2)
                return new
                {
                    userid = user.ID,
                    username = user.Name,
                    nickname = user.NickName,
                    sex = user2.Sex,
                    mail = user2.Mail,
                    mobile = user2.Mobile,
                    code = user2.Code,
                    roleid = user2.RoleID,
                    rolename = user2.RoleName,
                    avatar = user2.Avatar,
                };
            else
                return new
                {
                    userid = user.ID,
                    username = user.Name,
                    nickname = user.NickName,
                };
        }
        #endregion

        #region 辅助
        /// <summary>抓取远程头像</summary>
        /// <param name="user"></param>
        /// <returns></returns>
        public virtual Boolean FetchAvatar(IManageUser user)
        {
            var av = user.GetValue("Avatar") as String;
            if (av.IsNullOrEmpty()) throw new Exception("用户头像不存在 " + user);

            var url = av;
            if (!url.StartsWithIgnoreCase("http")) return false;

            // 不要扩展名
            var set = Setting.Current;
            av = set.AvatarPath.CombinePath(user.ID + "").GetFullPath();

            // 头像是否已存在
            if (File.Exists(av)) return false;

            av.EnsureDirectory(true);

            try
            {
                var wc = new WebClientX(true, true);
                Task.Run(() => wc.DownloadFileAsync(url, av)).Wait(5000);

                //// 更新头像
                //user.SetValue("Avatar", "/Sso/Avatar/" + user.ID);

                return true;
            }
            catch (Exception ex)
            {
                XTrace.WriteException(ex);
            }

            return false;
        }

        private Int32 GetRole(IDictionary<String, String> dic, Boolean create)
        {
            // 先找RoleName，再找RoleID
            if (dic.TryGetValue("RoleName", out var name))
            {
                var r = Role.FindByName(name);
                if (r != null) return r.ID;

                if (create)
                {
                    r = new Role { Name = name };
                    r.Insert();
                    return r.ID;
                }
            }

            if (dic.TryGetValue("RoleID", out var rid)) return rid.ToInt();

            return 0;
        }
        #endregion
    }
}