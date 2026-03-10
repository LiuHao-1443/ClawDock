import { useState, useEffect, useRef } from "react";
import { listen } from "@tauri-apps/api/event";
import { Loader2, Check, Trash2 } from "lucide-react";
import { useT } from "../../lib/i18n";
import { openclawUninstall, getPlatform, appExit } from "../../lib/tauri-commands";
import { Button } from "../ui/button";
import { Switch } from "../ui/switch";
import { Label } from "../ui/label";
import { ScrollArea } from "../ui/scroll-area";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from "../ui/dialog";

export function UninstallDialog({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const t = useT();
  const [removeDistro, setRemoveDistro] = useState(false);
  const [isWindows, setIsWindows] = useState(false);
  const [status, setStatus] = useState<"idle" | "running" | "done" | "error">("idle");
  const [errorMsg, setErrorMsg] = useState("");
  const [logs, setLogs] = useState<{ id: number; text: string }[]>([]);
  const logIdRef = useRef(0);

  useEffect(() => {
    getPlatform().then((p) => setIsWindows(p === "windows"));
  }, []);

  const MAX_LOG_LINES = 500;

  // Listen for progress logs
  useEffect(() => {
    if (!open) return;
    const unlisten = listen<string>("install-progress", (event) => {
      setLogs((prev) => {
        const next = [...prev, { id: ++logIdRef.current, text: event.payload }];
        return next.length > MAX_LOG_LINES ? next.slice(-MAX_LOG_LINES) : next;
      });
    });
    return () => {
      unlisten.then((fn) => fn());
    };
  }, [open]);

  // Auto-scroll logs
  const logEndRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    logEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [logs]);

  // Reset state when dialog opens
  useEffect(() => {
    if (open) {
      setStatus("idle");
      setLogs([]);
      setRemoveDistro(false);
      setErrorMsg("");
    }
  }, [open]);

  const handleUninstall = async () => {
    setStatus("running");
    try {
      await openclawUninstall(removeDistro);
      setStatus("done");
    } catch (e) {
      setErrorMsg(e instanceof Error ? e.message : String(e));
      setStatus("error");
    }
  };

  const handleExit = () => {
    appExit();
  };

  return (
    <Dialog open={open} onOpenChange={(v) => { if (status !== "running") onOpenChange(v); }}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle className="text-destructive">{t("uninstall.title")}</DialogTitle>
          <DialogDescription>{t("uninstall.desc")}</DialogDescription>
        </DialogHeader>

        {status === "idle" && (
          <div className="space-y-4 pt-2">
            {/* Fixed items */}
            <div className="space-y-2 text-sm">
              <div className="flex items-center gap-2 text-foreground">
                <Check className="h-4 w-4 text-primary shrink-0" />
                {t("uninstall.stopGateway")}
              </div>
              <div className="flex items-center gap-2 text-foreground">
                <Check className="h-4 w-4 text-primary shrink-0" />
                {t("uninstall.removeOpenclaw")}
              </div>
              <div className="flex items-center gap-2 text-foreground">
                <Check className="h-4 w-4 text-primary shrink-0" />
                {t("uninstall.resetState")}
              </div>
            </div>

            {/* Optional: remove WSL distro (Windows only) */}
            {isWindows && (
              <>
                <div className="border-t border-border" />
                <div className="flex items-start gap-3">
                  <Switch
                    checked={removeDistro}
                    onCheckedChange={setRemoveDistro}
                    className="mt-0.5"
                  />
                  <div>
                    <Label className="text-sm">{t("uninstall.removeDistro")}</Label>
                    <p className="text-xs text-muted-foreground mt-0.5">
                      {t("uninstall.removeDistroHint")}
                    </p>
                  </div>
                </div>
              </>
            )}

            <Button
              variant="destructive"
              onClick={handleUninstall}
              className="w-full gap-2"
            >
              <Trash2 className="h-4 w-4" />
              {t("uninstall.confirm")}
            </Button>
          </div>
        )}

        {status === "running" && (
          <div className="space-y-3 pt-2">
            <div className="flex items-center gap-2 text-sm text-muted-foreground">
              <Loader2 className="h-4 w-4 animate-spin" />
              {t("uninstall.running")}
            </div>
            <ScrollArea className="h-48 rounded-md border border-border bg-background p-3">
              <div className="font-mono text-xs space-y-0.5">
                {logs.map((log) => (
                  <div key={log.id} className="text-muted-foreground">{log.text}</div>
                ))}
                <div ref={logEndRef} />
              </div>
            </ScrollArea>
          </div>
        )}

        {status === "done" && (
          <div className="space-y-4 pt-2">
            {logs.length > 0 && (
              <ScrollArea className="h-32 rounded-md border border-border bg-background p-3">
                <div className="font-mono text-xs space-y-0.5">
                  {logs.map((log) => (
                    <div key={log.id} className="text-muted-foreground">{log.text}</div>
                  ))}
                </div>
              </ScrollArea>
            )}
            <div className="text-center space-y-1">
              <p className="text-sm font-medium text-foreground">{t("uninstall.complete")}</p>
              <p className="text-xs text-muted-foreground">{t("uninstall.completeHint")}</p>
            </div>
            <Button onClick={handleExit} variant="outline" className="w-full">
              {t("uninstall.exit")}
            </Button>
          </div>
        )}

        {status === "error" && (
          <div className="space-y-4 pt-2">
            {logs.length > 0 && (
              <ScrollArea className="h-32 rounded-md border border-border bg-background p-3">
                <div className="font-mono text-xs space-y-0.5">
                  {logs.map((log) => (
                    <div key={log.id} className="text-muted-foreground">{log.text}</div>
                  ))}
                </div>
              </ScrollArea>
            )}
            <div className="text-center space-y-1">
              <p className="text-sm font-medium text-destructive">{t("uninstall.failed")}</p>
              {errorMsg && (
                <p className="text-xs text-muted-foreground">{errorMsg}</p>
              )}
            </div>
            <Button onClick={() => setStatus("idle")} variant="outline" className="w-full">
              {t("uninstall.retry")}
            </Button>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}
