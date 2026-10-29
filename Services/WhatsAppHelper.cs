using System.Text.RegularExpressions;

namespace MenuScan.Services
{
    public static class WhatsAppHelper
    {
        /// <summary>
        /// Cleans a phone number into an international E.164 without '+' or spaces.
        /// Defaults 10-digit Indian numbers to +91.
        /// </summary>
        public static string? CleanPhoneNumber(string? rawPhone)
        {
            if (string.IsNullOrWhiteSpace(rawPhone)) return null;

            var digitsOnly = Regex.Replace(rawPhone, @"\D", "");
            if (string.IsNullOrEmpty(digitsOnly)) return null;

            if (digitsOnly.Length == 10)
            {
                return "91" + digitsOnly;
            }
            else if (digitsOnly.Length == 11 && digitsOnly.StartsWith("0"))
            {
                return "91" + digitsOnly.Substring(1);
            }
            else if (digitsOnly.Length >= 11 && digitsOnly.Length <= 13)
            {
                return digitsOnly;
            }

            return digitsOnly;
        }

        /// <summary>
        /// Builds a direct WhatsApp Web URL for browsers (works without Meta API).
        /// If forceWeb is true, opens web.whatsapp.com directly.
        /// </summary>
        public static string BuildWhatsAppUrl(string? rawPhone, string message, bool forceWeb = false)
        {
            var cleanPhone = CleanPhoneNumber(rawPhone);
            var encodedText = Uri.EscapeDataString(message);

            if (string.IsNullOrEmpty(cleanPhone))
            {
                // Generic share intent
                return $"https://api.whatsapp.com/send?text={encodedText}";
            }

            if (forceWeb)
            {
                return $"https://web.whatsapp.com/send?phone={cleanPhone}&text={encodedText}";
            }

            return $"https://api.whatsapp.com/send?phone={cleanPhone}&text={encodedText}";
        }

        public static string BuildOrderConfirmedMessage(string restName, string orderNumber, string tableNumber, decimal total, string trackerUrl)
        {
            return $"☕ *{restName}*\n" +
                   $"🎉 *Order Confirmed!*\n\n" +
                   $"• Order: *#{orderNumber}*\n" +
                   $"• Table: *{tableNumber}*\n" +
                   $"• Total: *₹{total:0.00}*\n\n" +
                   $"📍 *Live Order Tracker:*\n{trackerUrl}\n\n" +
                   $"Your fresh order is being prepared with care! 👨‍🍳";
        }

        public static string BuildFoodReadyMessage(string restName, string orderNumber, string tableNumber, string trackerUrl)
        {
            return $"🍽️ *{restName}*\n" +
                   $"✨ *Delicious News!*\n\n" +
                   $"Your order *#{orderNumber}* for *{tableNumber}* is fresh & *READY*!\n" +
                   $"Our server is bringing it to your table right now.\n\n" +
                   $"📍 *Order Tracker:*\n{trackerUrl}\n\n" +
                   $"Enjoy your meal! ☕✨";
        }

        public static string BuildBillReceiptMessage(string restName, string billNumber, string orderNumber, string tableNumber, decimal grandTotal, string paymentMethod, string receiptUrl)
        {
            return $"🧾 *{restName}*\n" +
                   $"*Official Dine-in Tax Receipt*\n\n" +
                   $"• Receipt No: *{billNumber}*\n" +
                   $"• Order: *#{orderNumber}*\n" +
                   $"• Table: *{tableNumber}*\n" +
                   $"• Amount Paid: *₹{grandTotal:0.00}* ({paymentMethod})\n\n" +
                   $"📄 *View Official Tax Receipt:*\n{receiptUrl}\n\n" +
                   $"Thank you for dining with us! Have a wonderful day! 🙏";
        }
    }
}
