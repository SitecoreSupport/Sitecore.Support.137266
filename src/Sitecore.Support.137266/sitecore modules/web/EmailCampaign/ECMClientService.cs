using Sitecore.Modules.EmailCampaign;
using Sitecore.Modules.EmailCampaign.Recipients;
using System;
using System.Web.Services;
using System.Web.Services.Protocols;

namespace Sitecore.Support.EmailCampaign.Cm.UI.sitecore_modules.Web.EmailCampaign
{
    public class ECMClientService : Sitecore.EmailCampaign.Cm.UI.sitecore_modules.Web.EmailCampaign.ECMClientService
    {
        [SoapHeader("AuthenticationInformation"), WebMethod(EnableSession = true)]
        public new void SendStandardMessage(Guid messageId, string recipientId, bool async)
        {
            RecipientId id = RecipientRepository.GetDefaultInstance().ResolveRecipientId(recipientId);
            if (id != null)
            {
                if (async)
                {
                    new Sitecore.Support.Modules.EmailCampaign.AsyncSendingManager(Factory.GetMessage(messageId.ToString())).SendStandardMessage(id);
                }
                else
                {
                    new Sitecore.Support.Modules.EmailCampaign.SendingManager(Factory.GetMessage(messageId.ToString())).SendStandardMessage(id);
                }
            }
        }
    }
}