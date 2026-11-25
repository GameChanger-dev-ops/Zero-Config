using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZeroConfigBridge.UI;

public static class AppStrings
{
    public static string CurrentLanguage { get; private set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase)
            ? "fr"
            : "en";

    public static void SetLanguage(string lang)
    {
        CurrentLanguage = lang.Equals("fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
    }

    public static string ToggleLanguage()
    {
        SetLanguage(CurrentLanguage == "fr" ? "en" : "fr");
        return CurrentLanguage;
    }

    public static string Get(string key)
    {
        var dict = CurrentLanguage == "fr" ? Fr : En;
        return dict.TryGetValue(key, out var val) ? val : (En.TryGetValue(key, out var fallback) ? fallback : key);
    }

    public static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
    {
        ["BrandSub"] = "PC VPN Bridge",
        ["NavBridge"] = "Bridge",
        ["NavTutorial"] = "Quick Tutorial",
        ["NavLogs"] = "Activity Logs",
        ["NavSupport"] = "Support & Studio",
        ["StatusDisconnected"] = "Disconnected",
        ["StatusDiscovering"] = "Discovering Android Hub...",
        ["StatusTun"] = "Creating VPNBridge TUN...",
        ["StatusHotspot"] = "Bridging Wi-Fi Hotspot...",
        ["StatusConnected"] = "Connected & Sharing VPN",
        ["StatusDisconnecting"] = "Disconnecting...",
        ["PipelineTitle"] = "LIVE CONNECTION PIPELINE",
        ["StepPhone"] = "1. Android Hub",
        ["StepTun"] = "2. VPNBridge TUN",
        ["StepHotspot"] = "3. Wi-Fi Hotspot",
        ["StateIdle"] = "Idle",
        ["StateScanning"] = "Scanning...",
        ["StateStarting"] = "Starting...",
        ["StateBridging"] = "Bridging...",
        ["StateConnected"] = "Connected",
        ["StateActive"] = "Active",
        ["StateSharing"] = "Sharing VPN",
        ["ConfigTitle"] = "BRIDGE CONFIGURATION",
        ["LblDiscovery"] = "Discovery Uplink",
        ["ModeAuto"] = "Auto (USB Tethering -> Wi-Fi mDNS -> ADB Fallback)",
        ["ModeUsb"] = "USB Tethering Only (Zero Wi-Fi Contention)",
        ["ModeWifi"] = "Wi-Fi / LAN mDNS Only",
        ["ModeAdb"] = "ADB USB Cable Fallback Only",
        ["LblSsid"] = "Hotspot SSID",
        ["LblPassword"] = "Password",
        ["ChkAutoReconnect"] = "Auto-reconnect tunnel automatically if phone VPN resets",
        ["BtnConnect"] = "Connect & Share VPN",
        ["BtnDisconnect"] = "Disconnect Bridge",
        ["BtnLeakTest"] = "Check IP / DNS Leak",
        ["TelemetryEmpty"] = "Uplink: --  •  Phone: --  •  Hotspot: --  •  Clients: 0",
        ["TelemetryFmt"] = "Uplink: {0}  •  Phone: {1}  •  Hotspot: {2}  •  Clients: {3}",
        ["TutTitle"] = "BEGINNER QUICK TUTORIAL",
        ["TutStepFmt"] = "Step {0} of 4",
        ["Tut1Title"] = "1. Turn On Your Phone VPN",
        ["Tut1Desc"] = "Start WireGuard, ProtonVPN, Mullvad, or OpenVPN on your Android phone first, then open Zero-Config Hub and tap 'Start Zero-Config Hub'.",
        ["Tut2Title"] = "2. Link Phone to Windows PC",
        ["Tut2Desc"] = "Plug in USB and turn on USB Tethering (recommended for maximum speed and zero Wi-Fi lag), or connect both to the same Wi-Fi, or enable ADB Debugging.",
        ["Tut3Title"] = "3. Click 'Connect & Share VPN'",
        ["Tut3Desc"] = "Zero-Config automatically discovers your phone, builds the Wintun virtual VPN adapter, starts your Windows Mobile Hotspot, and links Windows Internet Sharing.",
        ["Tut4Title"] = "4. Connect Any Device with Zero Setup",
        ["Tut4Desc"] = "Connect your Smart TV, PlayStation/Xbox, or secondary phone to the Windows Wi-Fi hotspot (default: ZeroConfig-VPN / zeroconfig123). No proxy settings required!",
        ["BtnPrev"] = "Previous",
        ["BtnNext"] = "Next Step",
        ["BtnStartNow"] = "Go to Bridge",
        ["LogsTitle"] = "DIAGNOSTICS & RECOVERY",
        ["BtnCopyLogs"] = "Copy Logs",
        ["BtnClearLogs"] = "Clear",
        ["BtnResetIcs"] = "Force Reset Windows ICS",
        ["SupportTitle"] = "CREATOR & 24/7 SUPPORT",
        ["MadeByLine"] = "Made by Game_Changer Studios — a 1001 company",
        ["SupportDesc"] = "Need help setting up USB Tethering, Smart TV connection, or Windows ICS? Reach us anytime on WhatsApp or phone:",
        ["BtnWhatsApp"] = "Open WhatsApp Support",
        ["BtnCopyPhone"] = "Copy +237 673533489",
        ["SidebarFooter1"] = "Made by Game_Changer Studios",
        ["SidebarFooter2"] = "a 1001 company",
        ["TrayOpen"] = "Open Zero-Config",
        ["TrayConnect"] = "Connect & Share VPN",
        ["TrayDisconnect"] = "Disconnect",
        ["TrayResetIcs"] = "Force Reset Windows ICS",
        ["TrayExit"] = "Exit Zero-Config",
        ["ToastCopied"] = "Copied to clipboard!",
        ["ToastIcsReset"] = "All Windows ICS sharing bindings were cleanly reset."
    };

    public static readonly IReadOnlyDictionary<string, string> Fr = new Dictionary<string, string>
    {
        ["BrandSub"] = "Pont VPN PC",
        ["NavBridge"] = "Pont VPN",
        ["NavTutorial"] = "Tutoriel Rapide",
        ["NavLogs"] = "Journaux",
        ["NavSupport"] = "Support & Studio",
        ["StatusDisconnected"] = "Déconnecté",
        ["StatusDiscovering"] = "Recherche du Hub Android...",
        ["StatusTun"] = "Création de VPNBridge TUN...",
        ["StatusHotspot"] = "Activation du Hotspot & ICS...",
        ["StatusConnected"] = "Connecté & Partage Actif",
        ["StatusDisconnecting"] = "Déconnexion...",
        ["PipelineTitle"] = "CHAÎNE DE CONNEXION EN DIRECT",
        ["StepPhone"] = "1. Hub Android",
        ["StepTun"] = "2. Tunnel Wintun",
        ["StepHotspot"] = "3. Hotspot Wi-Fi",
        ["StateIdle"] = "Inactif",
        ["StateScanning"] = "Recherche...",
        ["StateStarting"] = "Démarrage...",
        ["StateBridging"] = "Liaison...",
        ["StateConnected"] = "Connecté",
        ["StateActive"] = "Actif",
        ["StateSharing"] = "VPN Partagé",
        ["ConfigTitle"] = "CONFIGURATION DU PONT",
        ["LblDiscovery"] = "Mode de Liaison",
        ["ModeAuto"] = "Auto (Partage USB -> Wi-Fi mDNS -> Câble ADB)",
        ["ModeUsb"] = "Partage USB Uniquement (Performance Max)",
        ["ModeWifi"] = "Wi-Fi / LAN mDNS Uniquement",
        ["ModeAdb"] = "Câble USB ADB Uniquement",
        ["LblSsid"] = "Nom du Hotspot",
        ["LblPassword"] = "Mot de passe",
        ["ChkAutoReconnect"] = "Reconnecter automatiquement si le VPN du téléphone redémarre",
        ["BtnConnect"] = "Connecter & Partager le VPN",
        ["BtnDisconnect"] = "Déconnecter le Pont",
        ["BtnLeakTest"] = "Tester IP / Fuite DNS",
        ["TelemetryEmpty"] = "Liaison : --  •  Téléphone : --  •  Hotspot : --  •  Clients : 0",
        ["TelemetryFmt"] = "Liaison : {0}  •  Téléphone : {1}  •  Hotspot : {2}  •  Clients : {3}",
        ["TutTitle"] = "TUTORIEL RAPIDE DÉBUTANT",
        ["TutStepFmt"] = "Étape {0} sur 4",
        ["Tut1Title"] = "1. Activez le VPN sur votre téléphone",
        ["Tut1Desc"] = "Démarrez WireGuard, ProtonVPN, Mullvad ou OpenVPN sur Android, puis ouvrez Zero-Config Hub et appuyez sur 'Démarrer le Hub Zero-Config'.",
        ["Tut2Title"] = "2. Reliez le téléphone au PC Windows",
        ["Tut2Desc"] = "Branchez le câble USB et activez le Partage de connexion USB (recommandé pour une vitesse maximale), ou utilisez le même Wi-Fi, ou activez le débogage USB (ADB).",
        ["Tut3Title"] = "3. Cliquez sur 'Connecter & Partager le VPN'",
        ["Tut3Desc"] = "Zero-Config détecte votre téléphone, crée l'adaptateur virtuel Wintun, lance votre Point d'accès mobile Windows et partage le tunnel automatiquement.",
        ["Tut4Title"] = "4. Connectez vos appareils sans réglage",
        ["Tut4Desc"] = "Connectez votre Smart TV, console ou autre téléphone au Wi-Fi du PC (par défaut : ZeroConfig-VPN / zeroconfig123). Aucun réglage proxy requis !",
        ["BtnPrev"] = "Précédent",
        ["BtnNext"] = "Suivant",
        ["BtnStartNow"] = "Aller au Pont",
        ["LogsTitle"] = "DIAGNOSTIC & RÉPARATION",
        ["BtnCopyLogs"] = "Copier",
        ["BtnClearLogs"] = "Effacer",
        ["BtnResetIcs"] = "Réinitialiser Windows ICS",
        ["SupportTitle"] = "CRÉATEUR & SUPPORT 24/7",
        ["MadeByLine"] = "Créé par Game_Changer Studios — une entreprise 1001",
        ["SupportDesc"] = "Besoin d'aide avec le partage USB, votre Smart TV ou Windows ICS ? Contactez-nous sur WhatsApp ou par téléphone :",
        ["BtnWhatsApp"] = "Ouvrir WhatsApp Support",
        ["BtnCopyPhone"] = "Copier +237 673533489",
        ["SidebarFooter1"] = "Créé par Game_Changer Studios",
        ["SidebarFooter2"] = "une entreprise 1001",
        ["TrayOpen"] = "Ouvrir Zero-Config",
        ["TrayConnect"] = "Connecter & Partager",
        ["TrayDisconnect"] = "Déconnecter",
        ["TrayResetIcs"] = "Réinitialiser Windows ICS",
        ["TrayExit"] = "Quitter Zero-Config",
        ["ToastCopied"] = "Copié dans le presse-papiers !",
        ["ToastIcsReset"] = "Toutes les liaisons de partage Windows ICS ont été réinitialisées."
    };
}