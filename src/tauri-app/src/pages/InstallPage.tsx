import { useState, useEffect, useRef } from "react";
import { listen } from "@tauri-apps/api/event";
import { Check, Loader2, ChevronRight } from "lucide-react";
import {
  wslCheck,
  wslInstallDistro,
  openclawInstall,
  installStateMarkWsl2Done,
  installStateMarkOpenclawDone,
  getPlatform,
  configInstallDingtalk,
  openclawGetInstalledVersion,
} from "../lib/tauri-commands";
import type { WslCheckResult } from "../lib/types";
import { useT, useI18n } from "../lib/i18n";
import { Button } from "../components/ui/button";
import { ScrollArea } from "../components/ui/scroll-area";

type Step = "welcome" | "systemCheck" | "wslInstall" | "openclawInstall" | "complete";

interface InstallPageProps {
  onComplete: () => void;
}

export function InstallPage({ onComplete }: InstallPageProps) {
  const t = useT();
  const toggleLocale = useI18n((s) => s.toggleLocale);
  const [step, setStep] = useState<Step>("welcome");
  const [platform, setPlatform] = useState<string>("");
  const [wslResult, setWslResult] = useState<WslCheckResult | null>(null);
  const [openclawInstalled, setOpenclawInstalled] = useState(false);
  const [logs, setLogs] = useState<{ id: number; text: string }[]>([]);
  const logIdRef = useRef(0);
  const [error, setError] = useState<string | null>(null);
  const [installing, setInstalling] = useState(false);
  const logsEndRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    getPlatform().then(setPlatform);
  }, []);

  useEffect(() => {
    const unlisten = listen<string>("install-progress", (event) => {
      if (event.payload) {
        setLogs((prev) => {
          const next = [...prev, { id: ++logIdRef.current, text: event.payload }];
          return next.length > 200 ? next.slice(-200) : next;
        });
      }
    });
    return () => {
      unlisten.then((fn) => fn());
    };
  }, []);

  useEffect(() => {
    logsEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [logs]);

  const isWindows = platform === "windows";

  const handleSystemCheck = async () => {
    setStep("systemCheck");
    try {
      const ocVersion = await openclawGetInstalledVersion().catch(() => null);
      setOpenclawInstalled(!!ocVersion);
      if (isWindows) {
        const result = await wslCheck();
        setWslResult(result);
      } else {
        // Non-Windows: skip WSL checks, synthesize a passing result
        setWslResult({
          windowsVersionOk: true,
          virtualizationEnabled: true,
          wsl2Installed: true,
          distroInstalled: true,
          windowsBuild: "",
        });
      }
    } catch (e) {
      setError(String(e));
    }
  };

  const handleInstall = async () => {
    setInstalling(true);
    setError(null);
    setLogs([]);

    try {
      if (isWindows && wslResult && !wslResult.distroInstalled) {
        setStep("wslInstall");
        await wslInstallDistro();
        await installStateMarkWsl2Done();
      }

      setStep("openclawInstall");
      await openclawInstall();
      await configInstallDingtalk().catch(() => {});
      await installStateMarkOpenclawDone();
      setStep("complete");
    } catch (e) {
      setError(String(e));
    } finally {
      setInstalling(false);
    }
  };

  const steps: Step[] = isWindows
    ? ["welcome", "systemCheck", "wslInstall", "openclawInstall", "complete"]
    : ["welcome", "systemCheck", "openclawInstall", "complete"];

  const stepIndex = steps.indexOf(step);

  return (
    <div className="flex flex-col h-screen bg-background">
      {/* Top bar: language toggle only */}
      <div className="flex items-center justify-end px-6 py-3 shrink-0">
        <Button
          variant="outline"
          size="sm"
          onClick={toggleLocale}
          className="text-xs h-7"
        >
          {t("common.lang")}
        </Button>
      </div>

      {/* Main content */}
      <div className="flex-1 flex flex-col items-center justify-center px-8 pb-8 overflow-auto">
        <div className="w-full max-w-md">
          {/* Welcome */}
          {step === "welcome" && (
            <div className="text-center space-y-6">
              <img src="/logo-32.png" alt="ClawDock" className="w-16 h-16 mx-auto" />
              <div className="space-y-2">
                <h1 className="text-2xl font-bold text-foreground">
                  {t("install.welcome")}
                </h1>
                <p className="text-sm text-muted-foreground">
                  {t("install.desc")}
                </p>
                {isWindows && (
                  <p className="text-sm text-muted-foreground">
                    {t("install.descWsl")}
                  </p>
                )}
              </div>
              <Button size="lg" onClick={handleSystemCheck} className="gap-2">
                {t("install.getStarted")}
                <ChevronRight className="h-4 w-4" />
              </Button>
            </div>
          )}

          {/* System Check */}
          {step === "systemCheck" && (
            <div className="space-y-6">
              <div className="text-center space-y-1">
                <h2 className="text-xl font-semibold text-foreground">
                  {t("install.systemCheck")}
                </h2>
              </div>
              {wslResult ? (
                <div className="space-y-5">
                  <div className="rounded-xl border border-border/60 bg-card/50 divide-y divide-border/40">
                    {isWindows ? (
                      <>
                        <CheckRow
                          label={`${t("install.windowsBuild")}: ${wslResult.windowsBuild}`}
                          ok={wslResult.windowsVersionOk}
                        />
                        <CheckRow label={t("install.virtualization")} ok={wslResult.virtualizationEnabled} />
                        <CheckRow label={t("install.wsl2")} ok={wslResult.wsl2Installed} />
                        <CheckRow label={t("install.distro")} ok={wslResult.distroInstalled} />
                        <CheckRow label="OpenClaw" ok={openclawInstalled} />
                      </>
                    ) : (
                      <>
                        <CheckRow label={t("install.systemReady")} ok={true} />
                        <CheckRow label="OpenClaw" ok={openclawInstalled} />
                      </>
                    )}
                  </div>
                  <Button size="lg" onClick={handleInstall} className="w-full gap-2">
                    {isWindows && !wslResult.distroInstalled
                      ? t("install.installWslAndOc")
                      : t("install.installOc")}
                    <ChevronRight className="h-4 w-4" />
                  </Button>
                </div>
              ) : error ? (
                <p className="text-destructive text-sm text-center">{error}</p>
              ) : (
                <div className="flex items-center justify-center gap-2 py-8">
                  <Loader2 className="h-5 w-5 animate-spin text-primary" />
                  <span className="text-muted-foreground text-sm">{t("install.checking")}</span>
                </div>
              )}
            </div>
          )}

          {/* Installing */}
          {(step === "wslInstall" || step === "openclawInstall") && (
            <div className="space-y-4">
              <div className="text-center space-y-1">
                <h2 className="text-xl font-semibold text-foreground">
                  {step === "wslInstall"
                    ? t("install.installingWsl")
                    : t("install.installingOc")}
                </h2>
              </div>
              {installing && (
                <div className="flex items-center justify-center gap-2">
                  <Loader2 className="h-4 w-4 animate-spin text-primary" />
                  <span className="text-sm text-muted-foreground">{t("install.installing")}</span>
                </div>
              )}
              <div className="rounded-lg border border-border bg-card/30 overflow-hidden">
                <ScrollArea className="h-60">
                  <div className="p-3 font-mono text-xs text-muted-foreground space-y-0.5">
                    {logs.map((log) => (
                      <div key={log.id} className="whitespace-pre-wrap leading-relaxed">{log.text}</div>
                    ))}
                    <div ref={logsEndRef} />
                  </div>
                </ScrollArea>
              </div>
              {error && (
                <div className="p-3 bg-destructive/10 border border-destructive/30 rounded-lg text-destructive text-sm">
                  {error}
                </div>
              )}
            </div>
          )}

          {/* Complete */}
          {step === "complete" && (
            <div className="text-center space-y-6">
              <div className="w-16 h-16 mx-auto rounded-full bg-[hsl(var(--success))]/15 flex items-center justify-center">
                <Check className="h-8 w-8 text-[hsl(var(--success))]" />
              </div>
              <div className="space-y-2">
                <h2 className="text-2xl font-bold text-foreground">
                  {t("install.complete")}
                </h2>
                <p className="text-sm text-muted-foreground">
                  {t("install.completeDesc")}
                </p>
              </div>
              <Button size="lg" onClick={onComplete} className="gap-2">
                {t("install.launch")}
                <ChevronRight className="h-4 w-4" />
              </Button>
            </div>
          )}
        </div>

        {/* Step dots at bottom */}
        <div className="flex items-center gap-2 mt-8">
          {steps.map((_, i) => (
            <div
              key={i}
              className={`h-1.5 rounded-full transition-all ${
                i === stepIndex
                  ? "w-6 bg-primary"
                  : i < stepIndex
                    ? "w-1.5 bg-primary"
                    : "w-1.5 bg-border"
              }`}
            />
          ))}
        </div>
      </div>
    </div>
  );
}

function CheckRow({ label, ok }: { label: string; ok: boolean }) {
  return (
    <div className="flex items-center justify-between px-4 py-3">
      <span className="text-sm text-foreground">{label}</span>
      {ok ? (
        <div className="h-5 w-5 rounded-full bg-[hsl(var(--success))]/15 flex items-center justify-center">
          <Check className="h-3 w-3 text-[hsl(var(--success))]" />
        </div>
      ) : (
        <div className="h-5 w-5 rounded-full bg-destructive/15 flex items-center justify-center">
          <span className="text-[10px] font-bold text-destructive">!</span>
        </div>
      )}
    </div>
  );
}
