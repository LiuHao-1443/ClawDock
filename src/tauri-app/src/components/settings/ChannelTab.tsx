import { useState, useEffect } from "react";
import { Loader2, Check, AlertCircle, Save } from "lucide-react";
import { useConfig, useSaveChannel } from "../../hooks/useConfig";
import type { ChannelConfig } from "../../lib/types";
import { useT } from "../../lib/i18n";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { Label } from "../ui/label";
import { Switch } from "../ui/switch";
import { Separator } from "../ui/separator";
import { Badge } from "../ui/badge";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "../ui/select";
import { cn } from "../../lib/utils";

export function ChannelTab() {
  const t = useT();
  const { data: config, isLoading } = useConfig();
  const saveChannel = useSaveChannel();

  const [channels, setChannels] = useState<ChannelConfig[]>([]);
  const [saveStatus, setSaveStatus] = useState<Record<string, "idle" | "saving" | "success" | "error">>({});

  useEffect(() => {
    if (config) {
      setChannels(config.channels);
    }
  }, [config]);

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-20">
        <Loader2 className="h-5 w-5 animate-spin text-muted-foreground" />
      </div>
    );
  }

  const getChannel = (name: string): ChannelConfig => {
    return (
      channels.find((c) => c.name === name) || {
        name,
        enabled: false,
        properties: {},
      }
    );
  };

  const updateChannel = (name: string, updates: Partial<ChannelConfig>) => {
    setChannels((prev) => {
      const exists = prev.some((c) => c.name === name);
      if (exists) {
        return prev.map((c) => (c.name === name ? { ...c, ...updates } : c));
      }
      return [...prev, { name, enabled: false, properties: {}, ...updates }];
    });
  };

  const updateProperty = (
    channelName: string,
    key: string,
    value: string,
  ) => {
    setChannels((prev) =>
      prev.map((c) =>
        c.name === channelName
          ? { ...c, properties: { ...c.properties, [key]: value } }
          : c,
      ),
    );
  };

  const handleSave = async (channelName: string) => {
    setSaveStatus((prev) => ({ ...prev, [channelName]: "saving" }));
    try {
      const channel = getChannel(channelName);
      const failures = await saveChannel.mutateAsync(channel);
      if (failures.length > 0) {
        setSaveStatus((prev) => ({ ...prev, [channelName]: "error" }));
      } else {
        setSaveStatus((prev) => ({ ...prev, [channelName]: "success" }));
        setTimeout(() => setSaveStatus((prev) => ({ ...prev, [channelName]: "idle" })), 2000);
      }
    } catch {
      setSaveStatus((prev) => ({ ...prev, [channelName]: "error" }));
    }
  };

  const feishu = getChannel("feishu");
  const dingtalk = getChannel("dingtalk-connector");

  return (
    <div className="space-y-8">
      {/* Feishu */}
      <section>
        <ChannelHeader
          title={t("channel.feishu")}
          enabled={feishu.enabled}
          onToggle={(v) => updateChannel("feishu", { enabled: v })}
        />
        <div className="mt-4 space-y-3">
          <FormField
            label={t("channel.appId")}
            value={feishu.properties.appId || ""}
            onChange={(v) => updateProperty("feishu", "appId", v)}
          />
          <FormField
            label={t("channel.appSecret")}
            type="password"
            value={feishu.properties.appSecret || ""}
            onChange={(v) => updateProperty("feishu", "appSecret", v)}
          />
          <div className="grid grid-cols-2 gap-3">
            <FormField
              label={t("channel.connectionMode")}
              value={feishu.properties.connectionMode || "webhook"}
              onChange={(v) => updateProperty("feishu", "connectionMode", v)}
            />
            <FormField
              label={t("channel.verifyToken")}
              value={feishu.properties.verifyToken || ""}
              onChange={(v) => updateProperty("feishu", "verifyToken", v)}
            />
          </div>
          <SaveButton
            status={saveStatus["feishu"] || "idle"}
            onClick={() => handleSave("feishu")}
            label={t("channel.saveFeishu")}
          />
        </div>
      </section>

      <Separator />

      {/* DingTalk */}
      <section>
        <ChannelHeader
          title={t("channel.dingtalk")}
          enabled={dingtalk.enabled}
          onToggle={(v) => updateChannel("dingtalk-connector", { enabled: v })}
        />
        <div className="mt-4 space-y-3">
          <FormField
            label={t("channel.clientId")}
            value={dingtalk.properties.clientId || ""}
            onChange={(v) => updateProperty("dingtalk-connector", "clientId", v)}
          />
          <FormField
            label={t("channel.clientSecret")}
            type="password"
            value={dingtalk.properties.clientSecret || ""}
            onChange={(v) => updateProperty("dingtalk-connector", "clientSecret", v)}
          />
          <div className="grid grid-cols-2 gap-3">
            <PolicySelect
              label={t("channel.dmPolicy")}
              value={dingtalk.properties.dmPolicy || "open"}
              onChange={(v) => updateProperty("dingtalk-connector", "dmPolicy", v)}
            />
            <PolicySelect
              label={t("channel.groupPolicy")}
              value={dingtalk.properties.groupPolicy || "open"}
              onChange={(v) => updateProperty("dingtalk-connector", "groupPolicy", v)}
            />
          </div>
          <SaveButton
            status={saveStatus["dingtalk-connector"] || "idle"}
            onClick={() => handleSave("dingtalk-connector")}
            label={t("channel.saveDingtalk")}
          />
        </div>
      </section>
    </div>
  );
}

function ChannelHeader({
  title,
  enabled,
  onToggle,
}: {
  title: string;
  enabled: boolean;
  onToggle: (v: boolean) => void;
}) {
  const t = useT();
  return (
    <div className="flex items-center justify-between">
      <div className="flex items-center gap-2.5">
        <h3 className="text-sm font-medium text-foreground">{title}</h3>
        <Badge
          variant={enabled ? "default" : "secondary"}
          className={cn(
            "text-[10px] px-1.5 py-0 h-5",
            enabled && "bg-[hsl(var(--success))]"
          )}
        >
          {enabled ? t("channel.on") : t("channel.off")}
        </Badge>
      </div>
      <Switch checked={enabled} onCheckedChange={onToggle} />
    </div>
  );
}

function FormField({
  label,
  value,
  onChange,
  type = "text",
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  type?: string;
}) {
  return (
    <div className="space-y-1.5">
      <Label className="text-xs text-muted-foreground">{label}</Label>
      <Input
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </div>
  );
}

function PolicySelect({
  label,
  value,
  onChange,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
}) {
  const t = useT();
  return (
    <div className="space-y-1.5">
      <Label className="text-xs text-muted-foreground">{label}</Label>
      <Select value={value} onValueChange={onChange}>
        <SelectTrigger>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="open">{t("channel.policyOpen")}</SelectItem>
          <SelectItem value="close">{t("channel.policyClose")}</SelectItem>
          <SelectItem value="allowlist">{t("channel.policyAllowlist")}</SelectItem>
        </SelectContent>
      </Select>
    </div>
  );
}

function SaveButton({
  status,
  onClick,
  label,
  className,
}: {
  status: "idle" | "saving" | "success" | "error";
  onClick: () => void;
  label: string;
  className?: string;
}) {
  const t = useT();
  return (
    <Button
      size="sm"
      onClick={onClick}
      disabled={status === "saving"}
      className={cn(
        "gap-1.5 transition-all",
        status === "success" && "bg-[hsl(var(--success))] hover:bg-[hsl(var(--success))]/90",
        status === "error" && "bg-destructive hover:bg-destructive/90",
        className,
      )}
    >
      {status === "saving" ? (
        <><Loader2 className="h-3.5 w-3.5 animate-spin" /> {t("model.saving")}</>
      ) : status === "success" ? (
        <><Check className="h-3.5 w-3.5" /> {t("model.saved")}</>
      ) : status === "error" ? (
        <><AlertCircle className="h-3.5 w-3.5" /> {t("model.saveError")}</>
      ) : (
        <><Save className="h-3.5 w-3.5" /> {label}</>
      )}
    </Button>
  );
}
