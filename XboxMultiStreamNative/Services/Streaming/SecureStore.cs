using System;
using Windows.Foundation.Collections;
using Windows.Security.Credentials;
using Windows.Storage;

namespace Plura.Services.Streaming
{
    /// <summary>
    /// Guarda secretos (tokens de sesión y claves) con PasswordVault.
    /// Si el vault no está disponible en la consola, usa LocalSettings con
    /// ofuscación Base64 para no romper la app.
    /// </summary>
    public sealed class SecureStore
    {
        private const string FallbackPrefix = "plura.secure.";

        private readonly string _resource;

        public SecureStore(string resource)
        {
            _resource = string.IsNullOrWhiteSpace(resource) ? "Plura" : resource;
        }

        public string Get(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { return string.Empty; }

            try
            {
                var vault = new PasswordVault();
                var credential = vault.Retrieve(_resource, key);
                if (credential != null)
                {
                    credential.RetrievePassword();
                    return credential.Password ?? string.Empty;
                }
            }
            catch { }

            return ReadFallback(key);
        }

        public void Set(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key)) { return; }

            if (string.IsNullOrEmpty(value))
            {
                Remove(key);
                return;
            }

            try
            {
                var vault = new PasswordVault();
                try
                {
                    var existing = vault.Retrieve(_resource, key);
                    if (existing != null) { vault.Remove(existing); }
                }
                catch { }

                vault.Add(new PasswordCredential(_resource, key, value));
                return;
            }
            catch { }

            WriteFallback(key, value);
        }

        public void Remove(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { return; }

            try
            {
                var vault = new PasswordVault();
                var existing = vault.Retrieve(_resource, key);
                if (existing != null) { vault.Remove(existing); }
            }
            catch { }

            try
            {
                var values = Values;
                if (values != null) { values.Remove(FallbackPrefix + key); }
            }
            catch { }
        }

        private static IPropertySet Values
        {
            get
            {
                try { return ApplicationData.Current.LocalSettings.Values; }
                catch { return null; }
            }
        }

        private static string ReadFallback(string key)
        {
            try
            {
                var values = Values;
                if (values == null) { return string.Empty; }

                if (!values.TryGetValue(FallbackPrefix + key, out var raw) || raw == null)
                {
                    return string.Empty;
                }

                var text = raw as string;
                if (string.IsNullOrEmpty(text)) { return string.Empty; }

                try
                {
                    var bytes = Convert.FromBase64String(text);
                    return System.Text.Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    return text;
                }
            }
            catch { return string.Empty; }
        }

        private static void WriteFallback(string key, string value)
        {
            try
            {
                var values = Values;
                if (values == null) { return; }

                var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
                values[FallbackPrefix + key] = Convert.ToBase64String(bytes);
            }
            catch { }
        }
    }
}
