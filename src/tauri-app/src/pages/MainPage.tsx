import { useEffect, useRef, useState } from "react";
import { Loader2, ArrowUpCircle, CheckCircle2, AlertCircle, Cpu, Box, RefreshCw } from "lucide-react";
import { Toolbar } from "../components/layout/Toolbar";
import { StatusBar } from "../components/layout/StatusBar";
import { ConsoleDrawer } from "../components/layout/ConsoleDrawer";
import { SettingsPanel } from "../components/settings/SettingsPanel";
import { useGatewayStore } from "../stores/gateway-store";
import { useUiStore } from "../stores/ui-store";
import {
  gatewayDashboardUrl,
  gatewayStatus,
  gatewayLastError,
  gatewayOpenDashboard,
  gatewayHideDashboard,
  gatewaySetDashboardVisible,
  openclawGetInstalledVersion,
  openclawGetLatestVersion,
  openclawUpdate,
  configRead,
} from "../lib/tauri-commands";
import { useT } from "../lib/i18n";
import { Button } from "../components/ui/button";
import { Badge } from "../components/ui/badge";


export function MainPage() {
  const { status, setStatus, setDashboardUrl, setErrorMessage } =
    useGatewayStore();
  const { settingsOpen, consoleOpen } = useUiStore();
  const dashboardShown = useRef(false);

  // Poll gateway status every 5s
  useEffect(() => {
    const interval = setInterval(async () => {
      try {
        const s = await gatewayStatus();
        setStatus(s);
        if (s === "Running") {
          const url = await gatewayDashboardUrl();
          setDashboardUrl(url);
        }
        if (s === "Error") {
          const err = await gatewayLastError();
          setErrorMessage(err || null);
        }
      } catch {
        // ignore
      }
    }, 5000);

    return () => clearInterval(interval);
  }, [setStatus, setDashboardUrl, setErrorMessage]);

  // Auto-show/hide dashboard webview based on gateway status
  useEffect(() => {
    if (status === "Running") {
      // Mark as shown immediately so visibility effect can react while promise is in flight
      dashboardShown.current = true;
      // Small delay to ensure gateway is fully ready
      const timer = setTimeout(() => {
        gatewayOpenDashboard().catch(() => {
          dashboardShown.current = false;
        });
      }, 1000);
      return () => clearTimeout(timer);
    } else if (dashboardShown.current) {
      gatewayHideDashboard();
      dashboardShown.current = false;
    }
  }, [status]);

  // Hide dashboard webview when settings or console overlay is open
  useEffect(() => {
    if (!dashboardShown.current) return;
    const overlayOpen = settingsOpen || consoleOpen;
    gatewaySetDashboardVisible(!overlayOpen).catch(() => {});
  }, [settingsOpen, consoleOpen]);

  return (
    <div className="flex flex-col h-screen bg-background">
      <Toolbar />

      <div className="flex-1 relative overflow-hidden">
        {/* Content shown behind the dashboard webview (visible when not Running) */}
        <ContentArea status={status} />

        {settingsOpen && <SettingsPanel />}
        {consoleOpen && <ConsoleDrawer />}
      </div>

      <StatusBar />
    </div>
  );
}

function ContentArea({ status }: { status: string }) {
  const t = useT();
  const errorMessage = useGatewayStore((s) => s.errorMessage);

  if (status === "Running") {
    return (
      <div className="flex flex-col items-center justify-center h-full">
        <Loader2 className="h-6 w-6 animate-spin text-primary mb-3" />
        <p className="text-muted-foreground text-sm">{t("main.running")}</p>
      </div>
    );
  }

  if (status === "Starting") {
    return (
      <div className="flex flex-col items-center justify-center h-full">
        <Loader2 className="h-10 w-10 animate-spin text-primary mb-4" />
        <p className="text-muted-foreground">{t("main.starting")}</p>
      </div>
    );
  }

  if (status === "Error") {
    return (
      <div className="flex flex-col items-center justify-center h-full">
        <AlertCircle className="h-10 w-10 text-destructive mb-4" />
        <p className="text-destructive font-medium mb-2">{t("main.error")}</p>
        {errorMessage && (
          <p className="text-muted-foreground text-sm max-w-md text-center">
            {errorMessage}
          </p>
        )}
      </div>
    );
  }

  // Stopped state — show info panel
  return <InfoPanel />;
}

function InfoPanel() {
  const t = useT();
  const [installedVersion, setInstalledVersion] = useState<string | null>(null);
  const [latestVersion, setLatestVersion] = useState<string | null>(null);
  const [model, setModel] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [updating, setUpdating] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      setLoading(true);
      try {
        const [installed, latest, config] = await Promise.all([
          openclawGetInstalledVersion().catch(() => null),
          openclawGetLatestVersion().catch(() => null),
          configRead().catch(() => null),
        ]);
        if (!cancelled) {
          setInstalledVersion(installed);
          setLatestVersion(latest);
          setModel(config?.model?.primaryModel || null);
        }
      } catch {
        // ignore
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  const hasUpdate =
    installedVersion && latestVersion && installedVersion !== latestVersion;

  const handleUpdate = async () => {
    setUpdating(true);
    try {
      await openclawUpdate();
      // Refresh info after update
      const installed = await openclawGetInstalledVersion().catch(() => null);
      setInstalledVersion(installed);
    } catch {
      // ignore
    } finally {
      setUpdating(false);
    }
  };

  return (
    <div className="flex flex-col items-center justify-center h-full px-6">
      <div className="w-full max-w-sm space-y-4">
        {/* Title */}
        <div className="text-center mb-6">
          <p className="text-muted-foreground text-lg">{t("main.stopped")}</p>
          <p className="text-muted-foreground/60 text-sm mt-1">
            {t("main.stoppedHint")}
          </p>
        </div>

        {/* Info cards */}
        <div className="rounded-xl border border-border/60 bg-card/50 divide-y divide-border/40">
          {/* OpenClaw version */}
          <InfoRow
            icon={<Box className="h-4 w-4" />}
            label={t("info.openclawVersion")}
            loading={loading}
          >
            {installedVersion ? (
              <span className="text-sm text-foreground font-mono">v{installedVersion}</span>
            ) : (
              <span className="text-sm text-muted-foreground">{t("info.unknown")}</span>
            )}
          </InfoRow>

          {/* Configured model */}
          <InfoRow
            icon={<Cpu className="h-4 w-4" />}
            label={t("info.configuredModel")}
            loading={loading}
          >
            {model ? (
              <Badge variant="secondary" className="font-mono text-xs">
                {model}
              </Badge>
            ) : (
              <span className="text-sm text-muted-foreground">{t("info.noModel")}</span>
            )}
          </InfoRow>

          {/* Update status */}
          <InfoRow
            icon={<RefreshCw className="h-4 w-4" />}
            label={t("info.latestVersion")}
            loading={loading}
          >
            {latestVersion ? (
              <div className="flex items-center gap-2">
                <span className="text-sm text-foreground font-mono">v{latestVersion}</span>
                {hasUpdate ? (
                  <Badge variant="default" className="text-[10px] px-1.5 py-0 h-5 bg-[hsl(var(--warning))]">
                    {t("info.updateAvailable")}
                  </Badge>
                ) : installedVersion ? (
                  <CheckCircle2 className="h-3.5 w-3.5 text-[hsl(var(--success))]" />
                ) : null}
              </div>
            ) : (
              <span className="text-sm text-muted-foreground">{t("info.unknown")}</span>
            )}
          </InfoRow>
        </div>

        {/* Update button */}
        {hasUpdate && (
          <Button
            onClick={handleUpdate}
            disabled={updating}
            variant="outline"
            className="w-full gap-2"
          >
            {updating ? (
              <><Loader2 className="h-4 w-4 animate-spin" /> {t("info.updating")}</>
            ) : (
              <><ArrowUpCircle className="h-4 w-4" /> {t("info.update")} v{latestVersion}</>
            )}
          </Button>
        )}
      </div>
    </div>
  );
}

function InfoRow({
  icon,
  label,
  loading,
  children,
}: {
  icon: React.ReactNode;
  label: string;
  loading: boolean;
  children: React.ReactNode;
}) {
  return (
    <div className="flex items-center justify-between px-4 py-3">
      <div className="flex items-center gap-2.5 text-muted-foreground">
        {icon}
        <span className="text-sm">{label}</span>
      </div>
      {loading ? (
        <Loader2 className="h-3.5 w-3.5 animate-spin text-muted-foreground" />
      ) : (
        children
      )}
    </div>
  );
}
