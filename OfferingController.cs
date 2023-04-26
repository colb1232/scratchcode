using Alloy.Models;
using AsyncAwaitBestPractices;
using Demo.App_Code.AdminPortal;
using Demo.App_Code.Hubspot;
using Demo.App_Code.USNBusinessLogic;
using DocuSign.eSign.Model;
using FundAmerica;
using FundAmerica.Objects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Crypto.Agreement;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using System.Web.UI;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.Persistence;
using Umbraco.Core.Scoping;
using Umbraco.Core.Services;
using Umbraco.Web;
using Umbraco.Web.WebApi;
using USNWebsite.USNModels;

namespace Demo.App_Code.USNControllers
{
    public class FundAmericaApiController : UmbracoApiController
    {
        private readonly ProfilesService profilesService;
        private readonly AutoInvestService autoInvestService;
        readonly string key;
        readonly string webhookKey;
        readonly bool useSandbox;
        readonly int offeringsId;
        private readonly IScopeProvider scopeProvider;
        private readonly IContentService contentService;

        public FundAmericaApiController(IScopeProvider scopeProvider, ProfilesService profilesService, IContentService contentService, AutoInvestService autoInvestService)
        {
            this.scopeProvider = scopeProvider;
            this.profilesService = profilesService;
            this.contentService = contentService;
            this.autoInvestService = autoInvestService;

            var home = Umbraco
                .ContentAtRoot()
                .FirstOrDefault(x => x.ContentType.Alias == "USNHomepage");
            if (home != null)
            {
                var offerings = home.Children.FirstOrDefault(
                    x => x.ContentType.Alias == "offerings"
                );
                if (offerings != null)
                {
                    offeringsId = offerings.Id;
                }
            }

            var global = Umbraco
                .ContentAtRoot()
                .FirstOrDefault(x => x.ContentType.Alias == "USNWebsiteConfigurationSection");
            if (global != null)
            {
                var settings = global.Children.FirstOrDefault(
                    x => x.ContentType.Alias == "USNGlobalSettings"
                );
                if (settings != null)
                {
                    key = settings.Value<string>("fundAmericaKey");
                    webhookKey = settings.Value<string>("fundAmericaWebhookKey");
                    useSandbox = settings.Value<bool>("fundAmericaSandbox");
                }
            }
        }

        [HttpPost]
        public async Task<HttpResponseMessage> SendData()
        {
            string body = "";
            try
            {
                var ctx = HttpContext.Current;
                using (StreamReader sr = new StreamReader(ctx.Request.InputStream))
                    body = await sr.ReadToEndAsync().ConfigureAwait(false);
                NameValueCollection query = HttpUtility.ParseQueryString(body);

                var request = FundAmericaClient.ParseWebhook(query[0], webhookKey);

                if (request.ResourceType == "investment")
                {
                    using (var client = new FundAmericaClient(key, useSandbox))
                    {
                        var inv = await client.GetInvestmentAsync(request.Id).ConfigureAwait(false);
                        string offering = inv.OfferingUrl.Split('/').Last();
                        string investorId = inv.EntityUrl.Split('/').Last();
                        int profileId;
                        int offeringId;
                        IMember mbr;
                        Investments investment;

                        using (var scope = scopeProvider.CreateScope(autoComplete: true))
                        {
                            var sql = scope.SqlContext
                                .Sql()
                                .Select("*")
                                .From("Profiles")
                                .Where<ProfilesSubmission>(
                                    x => x.FundAmericaInvestorId == investorId
                                );
                            var profile = scope.Database
                                .Query<ProfilesSubmission>(sql)
                                .FirstOrDefault();
                            profileId = profile.Id;
                            mbr = Services.MemberService.GetById(profile.MemberId);
                        }
                        offeringId = contentService
                            .GetPagedChildren(offeringsId, 0, 100, out _)
                            .FirstOrDefault(x => x.GetValue<string>("fundAmericaID") == offering)
                            .Id;
                        using (var scope = scopeProvider.CreateScope(autoComplete: true))
                        {
                            var sql = scope.SqlContext
                                .Sql()
                                .Select("*")
                                .From("Investments")
                                .Where<Investments>(
                                    x =>
                                        x.ProfileID == profileId
                                        && x.OfferingId == offeringId.ToString()
                                );
                            investment = scope.Database.Query<Investments>(sql).FirstOrDefault();
                        }

                        if (investment != null)
                        {
                            investment.status = inv.Status;

                            using (var scope = scopeProvider.CreateScope(autoComplete: false))
                            {
                                scope.Database.Update(investment);
                                scope.Complete();
                            }
                        }
                        else
                        {
                            using (MailMessage m = new MailMessage())
                            {
                                m.To.Add("adam@mws.dev,colby@neighborhood.ventures");
                                m.Subject = "WebHook Notification - Investment";
                                m.Body =
                                    "investment not found\n\n\n"
                                    + JsonConvert.SerializeObject(
                                        JsonConvert.DeserializeObject(query[0]),
                                        Formatting.Indented
                                    )
                                    + "\n\n\nOffering: "
                                    + offeringId
                                    + "\nProfile: "
                                    + profileId
                                    + " ("
                                    + mbr.Name
                                    + " <"
                                    + mbr.Email
                                    + ">)\n\n\n"
                                    + JsonConvert.SerializeObject(inv, Formatting.Indented);
                                m.IsBodyHtml = false;

                                using (SmtpClient c = new SmtpClient())
                                    c.Send(m);
                            }
                        }
                        //using (MailMessage m = new MailMessage())
                        //{
                        //    m.To.Add("adam@mws.dev");
                        //    m.Subject = "WebHook Notification - Investment";
                        //    m.Body = JsonConvert.SerializeObject(JsonConvert.DeserializeObject(query[0]), Formatting.Indented) + "\n\n\n" + JsonConvert.SerializeObject(inv, Formatting.Indented);
                        //    m.IsBodyHtml = false;

                        //    using (SmtpClient c = new SmtpClient())
                        //        c.Send(m);
                        //}
                    }
                }

                //using (MailMessage m = new MailMessage())
                //{
                //    m.To.Add("adam@mws.dev");
                //    m.Subject = "WebHook Notification";
                //    m.Body = JsonConvert.SerializeObject(JsonConvert.DeserializeObject(query[0]), Formatting.Indented);
                //    m.IsBodyHtml = false;

                //    using (SmtpClient c = new SmtpClient())
                //        c.Send(m);
                //}
            }
            catch (Exception ex)
            {
                using (MailMessage m = new MailMessage())
                {
                    m.To.Add("adam@mws.dev");
                    m.Subject = "WebHook Notification Error";
                    m.Body = ex.ToString() + "\n\n\n" + body;
                    m.IsBodyHtml = false;

                    using (SmtpClient c = new SmtpClient())
                        c.Send(m);
                }
            }

            return this.Request.CreateResponse(HttpStatusCode.OK);
        }

        [HttpPost]
        public async Task<HttpResponseMessage> SendAlloyData()
        {
            string body = "";
            try
            {
                var ctx = HttpContext.Current;
                using (StreamReader sr = new StreamReader(ctx.Request.InputStream))
                    body = await sr.ReadToEndAsync().ConfigureAwait(false);

                dynamic review = JsonConvert.DeserializeObject(body);
                if (review != null)
                {
                    var profile = profilesService.GetProfileByAlloyId(
                        (string)review.data.journey_application_token
                    );
                    var alloy = new AlloyService(true);
                    dynamic app = await alloy
                        .GetApplication((string)review.data.journey_application_token)
                        .ConfigureAwait(false);
                    if (profile == null)
                    {
                        var profileId = ((JArray)app._embedded.child_entities)[0][
                            "external_entity_identifier"
                        ].Value<string>();
                        if (profileId != null)
                        {
                            profile = profilesService.GetProfile(Convert.ToInt32(profileId));
                            profile.AlloyId = (string)review.data.journey_application_token;
                        }
                    }
                    if (profile != null)
                    {
                        string status = "";
                        if (app.complete_outcome != null)
                            status = (string)app.complete_outcome;
                        else
                            status = (string)app.status;

                        profile.AlloyStatus = status;
                        profilesService.UpdateProfile(profile);
                    }
                }
            }
            catch (Exception ex)
            {
                using (MailMessage m = new MailMessage())
                {
                    m.To.Add("adam@mws.dev");
                    m.Subject = "WebHook Notification Error";
                    m.Body = ex.ToString() + "\n\n\n" + body;
                    m.IsBodyHtml = false;

                    using (SmtpClient c = new SmtpClient())
                        c.Send(m);
                }
            }

            return this.Request.CreateResponse(HttpStatusCode.OK);
        }

        public async Task<OfferingType> GetOffering(string id)
        {
            using (var client = new FundAmericaClient(key, useSandbox))
            {
                return await client.GetOfferingAsync(id);
            }
        }

        public async Task<Dictionary<string, string>> GetAgreementText()
        {
            using (var client = new FundAmericaClient(key, useSandbox))
            {
                return await client.GetAgreementTextAsync();
            }
        }

        public async Task<BankInfoType> GetBankInfo(string routingNumber)
        {
            try
            {
                using (var client = new FundAmericaClient(key, useSandbox))
                {
                    var bank = await client.GetBankInfoAsync(routingNumber);
                    return bank;
                }
            }
            catch
            {
                return new BankInfoType();
            }
        }

        [HttpPost]
        public async Task<SubscriptionAgreementType> CreateSubscriptionAgreement(
            string id,
            string amount
        )
        {
            var member = Members.GetCurrentMember();
            //var ctx = HttpContext.Current;
            SubscriptionRequestType req = new SubscriptionRequestType
            {
                EquityShareCount = "1",
                OfferingId = id,
                VestingAs =
                    member.GetProperty("firstName").Value<string>()
                    + " "
                    + member.GetProperty("lastName").Value<string>(),
                VestingAmount = amount,
                VestingAsEmail = member.GetProperty("Email").Value<string>()
            };
            using (var client = new FundAmericaClient(key, useSandbox))
            {
                return await client.CreateSubscriptionAgreement(req);
            }
        }

        [HttpPost]
        public async Task CreateACH()
        {
            var ctx = HttpContext.Current;
            string amount = ctx.Request["amount"];
            amount = amount.Replace(",", "");
            string id = ctx.Request["id"];
            string offeringId = ctx.Request["offeringId"];
            IPublishedContent m = Umbraco.Content(offeringId);
            string nameOnAccount = ctx.Request["nameOnAccount"];
            string accountType = ctx.Request["accountType"];
            string entityType = ctx.Request["entityType"];
            string accountNumber = ctx.Request["accountNumber"];
            string routingNumber = ctx.Request["routingNumber"];
            string profileId = ctx.Request["profileId"];
            string bankName = ctx.Request["bankName"];
           // bool autoInvest = Convert.ToBoolean(ctx.Request["autoInvest"]);
           // Decimal.TryParse(ctx.Request["monthlyAmount"], out decimal monthlyAmount);
            string ACHId = m.GetProperty("ACHId").GetValue() as string ?? "";
            string ip = ctx.Request.Headers["CF-Connecting-IP"] ?? ctx.Request.UserHostAddress;
            int accountId = -1;
            if (!String.IsNullOrWhiteSpace(ctx.Request["accountId"]))
            {
                accountId = Convert.ToInt32(ctx.Request["accountId"]);
                var account = profilesService.GetBankAccount(accountId);
                nameOnAccount = account.NameOnAccount;
                accountNumber = account.AccountNumber;
                accountType = account.AccountType;
                entityType = account.AccountClass;
                routingNumber = account.RoutingNumber;
            }

            Int32.TryParse(offeringId, out int parsed);
            IPublishedContent currentOffering = Umbraco.Content(parsed.ToString());
            // check if they can invest
            List<Investments> investmentsBefore = GetInvestmentsByProfileAndOfferingId(offeringId);
            decimal totalAmountBefore = investmentsBefore
                .Where(x => x.status == "completed" || x.status == "pending")
                .Sum(x => x.Amount);
            //IPublishedContent readOffering = Umbraco.Content(id);
            decimal maximumRaise = currentOffering.GetProperty("maximumRaise").Value<decimal>();
            Decimal amountPaidRightNow = Decimal.Parse(amount);

            if (totalAmountBefore + amountPaidRightNow > maximumRaise)
            {
                // "catch you on the flippy flop" - Heidi
                throw new Exception(
                    "There was an error creating your investment. The Offering Maximum was reached. Please check out our other offerings."
                );
            }
            // end check
            if (String.IsNullOrWhiteSpace(ip))
                ip = ctx.Request.ServerVariables["REMOTE_HOST"];
            var member = Members.GetCurrentMember();
            var p = profilesService.GetProfile(Convert.ToInt32(profileId));
            var iProfile = profilesService
                .GetProfiles(p.MemberId)
                .FirstOrDefault(x => x.ProfileType == "Individual");
            var email = p.Email;
            if (String.IsNullOrWhiteSpace(email))
                email = iProfile.Email;
            if (String.IsNullOrWhiteSpace(email))
                email = member.Value<string>("Email");

            if (accountId == -1)
                accountId = profilesService.AddAccount(
                    new BankAccountInformation
                    {
                        MemberId = p.MemberId,
                        ProfileId = p.Id,
                        BankName = bankName,
                        NameOnAccount = nameOnAccount,
                        AccountNumber = accountNumber,
                        RoutingNumber = routingNumber,
                        AccountClass = entityType,
                        AccountType = accountType,
                        OneTimeWithdrawal = true // need secret message
                    }
                );

            if (String.IsNullOrEmpty(p.AlloyId) || String.IsNullOrWhiteSpace(p.AlloyStatus))
            {
                Entity entity;
                if (p.ProfileType == "Individual")
                {
                    entity = new PersonEntity
                    {
                        FirstName = member.Value<string>("firstName"),
                        LastName = member.Value<string>("lastName"),
                        Address1 = p.Address,
                        City = p.City,
                        State = p.State,
                        PostalCode = p.ZipCode,
                        SSN = p.TIN,
                        BirthDate = String.Format("{0:yyyy-MM-dd}", p.DateOfBirth),
                        EmailAddress = p.Email,
                        PhoneNumber = p.Phone
                    };
                }
                else
                {
                    entity = new BusinessEntity
                    {
                        FirstName = member.Value<string>("firstName"),
                        LastName = member.Value<string>("lastName"),
                        BusinessName = p.Nickname,
                        Address1 = p.Address,
                        City = p.City,
                        State = p.State,
                        PostalCode = p.ZipCode,
                        EIN = p.TIN,
                    };
                }

                var alloy = new AlloyService(true);
                var resp = await alloy
                    .CreateApplication(
                        new ApplicationRequest
                        {
                            Entities = new List<EntityData>
                            {
                                new EntityData
                                {
                                    Data = entity,
                                    BranchName =
                                        p.ProfileType == "Individual" ? "persons" : "businesses",
                                    EntityType =
                                        p.ProfileType == "Individual" ? "person" : "business",
                                    ExternalEntityID = p.Id.ToString()
                                }
                            }
                        }
                    )
                    .ConfigureAwait(false);

                if (resp != null)
                {
                    if (resp.journey_application_token != null)
                    {
                        p.AlloyId = resp.journey_application_token;
                        p.AlloyStatus = resp.journey_application_status;

                        profilesService.UpdateProfile(p);
                    }
                }
            }

            string sharePrice = "";
            if (currentOffering != null)
                sharePrice = (string)currentOffering.GetProperty("SharePrice").Value();

            await CreateInvestmentEntry(
                member.Id,
                profileId,
                amount,
                null,
                "pending",
                ACHId,
                parsed,
                sharePrice,
                accountId: accountId,
                offeringName: currentOffering.Name,
                profileName: p.Nickname
              //  autoInvest: autoInvest,
              //  monthlyAmount: monthlyAmount,
            );

            var numShares =
                amountPaidRightNow / Int64.Parse(currentOffering.Value<string>("sharePrice"));
            // SigningService service = new SigningService();
            //service.BuildEnvelope(email, p.Nickname, numShares.ToString(), amount);

            //if (currentOffering.Value<string>("offeringType") != "Regulation A")
            //{
            //    service.BuildVo17Envelope(
            //        email,
            //        p.Nickname,
            //        numShares.ToString(),
            //        amount,
            //        p.Address,
            //        p.TIN,
            //        p.Phone,
            //        p.City,
            //        p.State
            //    );
            //}
            //else // REIT Docusign
            //{
            //    service.BuildEnvelope(email, p.Nickname, numShares.ToString(), amount);
            //    //if (autoInvest && monthlyAmount > 0)
            //    //{
            //    //    service.BuildAutoInvestEnvelope(
            //    //         email,
            //    //         p.Nickname,
            //    //         monthlyAmount.ToString()
            //    //     );
            //    //}
            //}

            // now that the investment was made lets update the offering to be successful in dropdown
            List<Investments> investments = GetInvestmentsByProfileAndOfferingId(offeringId);
            decimal totalAmount = investments
                .Where(x => x.status == "completed" || x.status == "pending")
                .Sum(x => x.Amount);
            Decimal currentAmountLeft = 0; // need to figure this out
            currentAmountLeft =
                currentOffering.GetProperty("maximumRaise").Value<decimal>() - totalAmount;
            if (currentAmountLeft - amountPaidRightNow <= 0)
            {
                IContent content = contentService.GetById(parsed);
                content.SetValue("projectStatus", "Successful");
                contentService.SaveAndPublish(content);
            }
            //return agreement;
        }

        public void CreateAutoInvest()
        {
            var ctx = HttpContext.Current;
            bool autoInvest = Convert.ToBoolean(ctx.Request["autoInvest"]);
            string offeringId = ctx.Request["offeringId"];
            string profileId = ctx.Request["profileId"];

            if (autoInvest)
            {
                autoInvestService.AddAutoInvest(new AutoInvest
                {
                    ProfileId = Convert.ToInt32(profileId),
                    InvestDate = DateTime.Now,
                    OfferingId = Convert.ToInt32(offeringId),
                    InvestAmount = 0
                });
            }
        }

        public List<Investments> GetInvestmentsByProfileAndOfferingId(string offeringId)
        {
            //List<Investments> result;
            using (var scope = scopeProvider.CreateScope(autoComplete: true))
            {
                var sql = scope.SqlContext
                    .Sql()
                    .Select("*")
                    .From("Investments")
                    .Where<Investments>(x => x.OfferingId == offeringId);
                return scope.Database.Query<Investments>(sql).ToList(); //.Skip((page - 1) * 20).Take(20);
            }
            //return result = await myTask;
        }

        public async Task CreateInvestmentEntry(
            int memberId,
            string profileId,
            string amount,
            string investmentId,
            string status,
            string ACHId,
            int offeringId = 0,
            string sharePrice = "",
            int? accountId = null,
            string offeringName = null,
            string profileName = null            
           // bool autoInvest = false,
           // decimal monthlyAmount = 0,
        )

        {
            var invAmt = Decimal.Parse(amount);
            // called surface controller can use current scope
            using (var scope = scopeProvider.CreateScope())
            {
                scope.Database.Execute(
                    "INSERT INTO Investments (MemberId, ProfileID, Amount, [DateTime], InvestmentId, [status], OfferingId, SharePrice, AccountId, OfferingName, ProfileName, ACHId) VALUES (@0, @1, @2, GETDATE(), @3, @4, @5, @6, @7, @8, @9, @10)",
                    memberId,
                    profileId,
                    invAmt,
                    investmentId,
                    status,
                    offeringId,
                    sharePrice,
                    accountId,
                    offeringName,
                    profileName,
                    ACHId
                );
                scope.Complete();
            }

            //if (autoInvest && monthlyAmount >= 100)
            //{
            //    using (var scope = scopeProvider.CreateScope())
            //    {
            //        scope.Database.Execute(
            //            "INSERT INTO AutoInvest (OfferingId, InvestAmount, InvestDate, AccountId) VALUES (@0, @1, GETDATE(), @2)",
            //            offeringId,
            //            monthlyAmount,
            //            accountId
            //        );
            //        scope.Complete();
            //    }
            //}

            var p = profilesService.GetProfile(Convert.ToInt32(profileId));
            string email = p.Email;
            if (String.IsNullOrWhiteSpace(email))
            {
                var member = Umbraco.Member(memberId);
                email = member.Value<string>("Email");
            }

            // BEGIN HUBSPOT
            IPublishedContent m = Umbraco.Content(offeringId);
            if (m.GetProperty("hubspotName").HasValue())
            {
                var amt = profilesService.GetInvestmentTotalByOffering(p.Id, offeringId.ToString());
                var props = new Dictionary<string, string>
                {
                    { m.Value<string>("hubspotName"), amt.ToString("F0") },
                   // { "auto_invest_amount", monthlyAmount.ToString("F0") }
                };
              //  props.Add("auto_invest", autoInvest ? "true" : "false");
                var hc = new HubspotClient();
                await hc.UpdateContact(email, props);
            }
        }

        // END HUBSPOT

        [HttpPost]
        public async Task CreateWire()
        {
            var ctx = HttpContext.Current;
            string amount = ctx.Request["amount"];
            amount = amount.Replace(",", "");
            string id = ctx.Request["id"];
            string offeringId = ctx.Request["offeringId"];
            IPublishedContent m = Umbraco.Content(offeringId);
            string profileId = ctx.Request["profileId"];
            // string ACHId = (string)m.GetProperty("ACHId").GetValue();
            var member = Members.GetCurrentMember();
            var p = profilesService.GetProfile(Convert.ToInt32(profileId));

            Int32.TryParse(offeringId, out int parsed);
            IPublishedContent currentOffering = Umbraco.Content(parsed.ToString());

            // check if they can invest
            List<Investments> investmentsBefore = GetInvestmentsByProfileAndOfferingId(offeringId);
            decimal totalAmountBefore = investmentsBefore
                .Where(x => x.status == "completed" || x.status == "pending")
                .Sum(x => x.Amount);
            //IPublishedContent readOffering = Umbraco.Content(id);
            decimal maximumRaise = currentOffering.GetProperty("maximumRaise").Value<decimal>();
            long amountPaidRightNow = Int64.Parse(amount);

            if (totalAmountBefore + amountPaidRightNow > maximumRaise)
            {
                // "catch you on the flippy flop" - Heidi
                throw new Exception(
                    "There was an error creating your investment. The Offering Maximum was reached. Please check out our other offerings."
                );
            }
            // end check

            if (String.IsNullOrEmpty(p.AlloyId) || String.IsNullOrWhiteSpace(p.AlloyStatus))
            {
                Entity entity;
                if (p.ProfileType == "Individual")
                {
                    entity = new PersonEntity
                    {
                        FirstName = member.Value<string>("firstName"),
                        LastName = member.Value<string>("lastName"),
                        Address1 = p.Address,
                        City = p.City,
                        State = p.State,
                        EmailAddress = p.Email,
                        PhoneNumber = p.Phone,
                        PostalCode = p.ZipCode,
                        SSN = p.TIN,
                        BirthDate = String.Format("{0:yyyy-MM-dd}", p.DateOfBirth)
                    };
                }
                else
                {
                    entity = new BusinessEntity
                    {
                        FirstName = member.Value<string>("firstName"),
                        LastName = member.Value<string>("lastName"),
                        BusinessName = p.Nickname,
                        Address1 = p.Address,
                        City = p.City,
                        State = p.State,
                        PostalCode = p.ZipCode,
                        EIN = p.TIN,
                    };
                }

                var alloy = new AlloyService(true);
                var resp = await alloy
                    .CreateApplication(
                        new ApplicationRequest
                        {
                            Entities = new List<EntityData>
                            {
                                new EntityData
                                {
                                    Data = entity,
                                    BranchName =
                                        p.ProfileType == "Individual" ? "persons" : "businesses",
                                    EntityType =
                                        p.ProfileType == "Individual" ? "person" : "business",
                                    ExternalEntityID = p.Id.ToString()
                                }
                            }
                        }
                    )
                    .ConfigureAwait(false);

                if (resp != null)
                {
                    if (resp.journey_application_token != null)
                    {
                        p.AlloyId = resp.journey_application_token;
                        p.AlloyStatus = resp.journey_application_status;

                        profilesService.UpdateProfile(p);
                    }
                }
            }

            var iProfile = profilesService
                .GetProfiles(p.MemberId)
                .FirstOrDefault(x => x.ProfileType == "Individual");
            var email = p.Email;
            if (String.IsNullOrWhiteSpace(email))
                email = iProfile.Email;
            if (String.IsNullOrWhiteSpace(email))
                email = member.Value<string>("Email");

            var numShares =
                amountPaidRightNow / Int64.Parse(currentOffering.Value<string>("sharePrice"));
            //SigningService service = new SigningService();

            //if (currentOffering.Value<string>("offeringType") != "Regulation A")
            //{
            //    service.BuildVo17Envelope(
            //        iProfile.Email,
            //        p.Nickname,
            //        numShares.ToString(),
            //        amount,
            //        p.Address,
            //        p.TIN,
            //        p.Phone,
            //        p.City,
            //        p.State
            //    );
            //}
            //else // REIT Docusign
            //{
            //    service.BuildEnvelope(iProfile.Email, p.Nickname, numShares.ToString(), amount);
            //}

            //  service.BuildEnvelope(p.Email ?? member.Value<string>("Email"), p.Nickname, numShares.ToString(), amount);

            string sharePrice = "";
            if (currentOffering != null)
                sharePrice = (string)currentOffering.GetProperty("SharePrice").Value();
            await CreateInvestmentEntry(
                member.Id,
                profileId,
                amount,
                id,
                "pending",
                null,
                parsed,
                sharePrice,
                offeringName: currentOffering.Name,
                profileName: p.Nickname
            );
            // now that the investment was made lets update the offering to be successful in dropdown
            List<Investments> investments = GetInvestmentsByProfileAndOfferingId(offeringId);
            decimal totalAmount = investments
                .Where(x => x.status == "completed" || x.status == "pending")
                .Sum(x => x.Amount);
            Decimal currentAmountLeft = 0; // need to figure this out
            currentAmountLeft =
                currentOffering.GetProperty("maximumRaise").Value<decimal>() - totalAmount;
            if (currentAmountLeft - amountPaidRightNow <= 0)
            {
                IContent content = contentService.GetById(parsed);
                content.SetValue("projectStatus", "Successful");
                contentService.SaveAndPublish(content);
            }
        }
    }
}
