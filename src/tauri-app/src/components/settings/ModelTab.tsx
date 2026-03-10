import { useState, useEffect, useMemo } from "react";
import { X, Plus, Pencil, Trash2, Loader2, Check, AlertCircle, Save, Eye, EyeOff } from "lucide-react";
import { useConfig, useAvailableModels, useSaveModelConfig, useSaveProvider, useDeleteProvider, useSaveProviderApiKey } from "../../hooks/useConfig";
import type { ModelConfig, ProviderConfig, ProviderModel } from "../../lib/types";
import { useT } from "../../lib/i18n";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { Label } from "../ui/label";
import { Separator } from "../ui/separator";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "../ui/select";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from "../ui/dialog";
import { cn } from "../../lib/utils";

export function ModelTab() {
  const t = useT();
  const { data: config, isLoading } = useConfig();
  const { data: availableModels } = useAvailableModels();
  const saveModelConfig = useSaveModelConfig();
  const saveProvider = useSaveProvider();
  const deleteProvider = useDeleteProvider();
  const saveApiKey = useSaveProviderApiKey();

  const [primaryModel, setPrimaryModel] = useState("");
  const [fallbacks, setFallbacks] = useState<{ id: string; value: string }[]>([]);
  const [providers, setProviders] = useState<ProviderConfig[]>([]);
  const [editingProvider, setEditingProvider] = useState<ProviderConfig | null>(null);
  const [saveStatus, setSaveStatus] = useState<"idle" | "saving" | "success" | "error">("idle");
  const [pendingApiKeys, setPendingApiKeys] = useState<Record<string, string>>({});

  useEffect(() => {
    if (config) {
      setPrimaryModel(config.model.primaryModel);
      setFallbacks(config.model.fallbackModels.map((v) => ({ id: crypto.randomUUID(), value: v })));
      setProviders(config.providers);
    }
  }, [config]);

  // Build provider → models map from available models list
  const providerModelsMap = useMemo(() => {
    const map: Record<string, string[]> = {};
    if (availableModels) {
      for (const m of availableModels) {
        const slashIdx = m.indexOf("/");
        if (slashIdx > 0) {
          const provider = m.substring(0, slashIdx);
          const model = m.substring(slashIdx + 1);
          if (!map[provider]) map[provider] = [];
          map[provider].push(model);
        }
      }
    }
    // Also include configured custom providers
    for (const p of providers) {
      if (!map[p.name]) map[p.name] = [];
      for (const m of p.models) {
        const modelId = m.id || m.name;
        if (modelId && !map[p.name].includes(modelId)) {
          map[p.name].push(modelId);
        }
      }
    }
    return map;
  }, [availableModels, providers]);

  const providerNames = useMemo(() => Object.keys(providerModelsMap).sort(), [providerModelsMap]);

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-20">
        <Loader2 className="h-5 w-5 animate-spin text-muted-foreground" />
      </div>
    );
  }

  const handleSave = async () => {
    setSaveStatus("saving");
    try {
      const isValidModel = (v: string) => v.trim() !== "" && !v.endsWith("/");
      const modelConfig: ModelConfig = {
        primaryModel: primaryModel.endsWith("/") ? "" : primaryModel,
        fallbackModels: fallbacks.map((f) => f.value).filter(isValidModel),
      };
      const failures = await saveModelConfig.mutateAsync(modelConfig);
      // Save any pending API keys
      for (const [providerName, key] of Object.entries(pendingApiKeys)) {
        if (key.trim()) {
          await saveApiKey.mutateAsync({ providerName, apiKey: key.trim() });
        }
      }
      if (failures.length > 0) {
        setSaveStatus("error");
      } else {
        setPendingApiKeys({});
        setSaveStatus("success");
        setTimeout(() => setSaveStatus("idle"), 2000);
      }
    } catch {
      setSaveStatus("error");
    }
  };

  const handleAddFallback = () => {
    setFallbacks([...fallbacks, { id: crypto.randomUUID(), value: "" }]);
  };

  const handleRemoveFallback = (id: string) => {
    setFallbacks(fallbacks.filter((f) => f.id !== id));
  };

  const handleSaveProvider = async (provider: ProviderConfig) => {
    try {
      await saveProvider.mutateAsync(provider);
      if (provider.apiKey) {
        await saveApiKey.mutateAsync({
          providerName: provider.name,
          apiKey: provider.apiKey,
        });
      }
      setEditingProvider(null);
    } catch (e) {
      console.error("Failed to save provider:", e);
      setSaveStatus("error");
    }
  };

  const handleDeleteProvider = async (name: string) => {
    try {
      await deleteProvider.mutateAsync(name);
    } catch (e) {
      console.error("Failed to delete provider:", e);
      setSaveStatus("error");
    }
  };

  const handleApiKeyChange = (providerName: string, apiKeyValue: string) => {
    setPendingApiKeys((prev) => ({ ...prev, [providerName]: apiKeyValue }));
  };

  return (
    <div className="space-y-8">
      {/* Primary Model */}
      <section>
        <SectionHeader
          title={t("model.primary")}
          description={t("model.primaryDesc")}
        />
        <div className="mt-3">
          <ModelSelector
            value={primaryModel}
            onChange={setPrimaryModel}
            providerModelsMap={providerModelsMap}
            providerNames={providerNames}
            providers={providers}
            onApiKeyChange={handleApiKeyChange}
          />
        </div>
      </section>

      <Separator />

      {/* Fallback Models */}
      <section>
        <SectionHeader
          title={t("model.fallbacks")}
          description={t("model.fallbacksDesc")}
        />
        <div className="mt-3 space-y-3">
          {fallbacks.map((fb) => (
            <div key={fb.id} className="group">
              <div className="flex gap-2">
                <div className="flex-1">
                  <ModelSelector
                    value={fb.value}
                    onChange={(v) => {
                      setFallbacks((prev) =>
                        prev.map((f) => (f.id === fb.id ? { ...f, value: v } : f)),
                      );
                    }}
                    providerModelsMap={providerModelsMap}
                    providerNames={providerNames}
                    providers={providers}
                    onApiKeyChange={handleApiKeyChange}
                  />
                </div>
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => handleRemoveFallback(fb.id)}
                  className="h-9 w-9 self-end text-muted-foreground hover:text-destructive opacity-0 group-hover:opacity-100 transition-opacity"
                >
                  <X className="h-4 w-4" />
                </Button>
              </div>
            </div>
          ))}
          <Button
            variant="ghost"
            size="sm"
            onClick={handleAddFallback}
            className="text-xs text-muted-foreground hover:text-primary gap-1.5 px-2 h-8"
          >
            <Plus className="h-3.5 w-3.5" />
            {t("model.addFallback")}
          </Button>
        </div>
      </section>

      <Separator />

      {/* Custom Providers */}
      <section>
        <SectionHeader
          title={t("model.providers")}
          description={t("model.providersDesc")}
        />
        <div className="mt-3 space-y-2">
          {providers.map((p) => (
            <div
              key={p.name}
              className="group flex items-center justify-between p-3 rounded-lg border border-border/60 hover:border-border transition-colors"
            >
              <div className="min-w-0">
                <div className="text-sm text-foreground font-medium truncate">
                  {p.name}
                </div>
                <div className="text-xs text-muted-foreground mt-0.5 truncate">
                  {p.baseUrl || t("model.noUrl")} · {p.models.length} {t("model.models")}
                </div>
              </div>
              <div className="flex gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => setEditingProvider({ ...p })}
                  className="h-7 w-7 text-muted-foreground hover:text-foreground"
                >
                  <Pencil className="h-3.5 w-3.5" />
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => handleDeleteProvider(p.name)}
                  className="h-7 w-7 text-muted-foreground hover:text-destructive"
                >
                  <Trash2 className="h-3.5 w-3.5" />
                </Button>
              </div>
            </div>
          ))}
          <Button
            variant="ghost"
            size="sm"
            onClick={() =>
              setEditingProvider({
                name: "",
                baseUrl: "",
                apiKey: "",
                api: "openai",
                models: [],
              })
            }
            className="text-xs text-muted-foreground hover:text-primary gap-1.5 px-2 h-8"
          >
            <Plus className="h-3.5 w-3.5" />
            {t("model.addProvider")}
          </Button>
        </div>
      </section>

      {/* Provider Edit Dialog */}
      <Dialog
        open={!!editingProvider}
        onOpenChange={(open) => {
          if (!open) setEditingProvider(null);
        }}
      >
        <DialogContent className="max-w-lg max-h-[80vh] overflow-auto">
          {editingProvider && (
            <ProviderForm
              provider={editingProvider}
              onSave={handleSaveProvider}
              onCancel={() => setEditingProvider(null)}
            />
          )}
        </DialogContent>
      </Dialog>

      <Separator />

      {/* Save button */}
      <Button
        onClick={handleSave}
        disabled={saveStatus === "saving"}
        className={cn(
          "w-full gap-2 transition-all",
          saveStatus === "success" && "bg-[hsl(var(--success))] hover:bg-[hsl(var(--success))]/90",
          saveStatus === "error" && "bg-destructive hover:bg-destructive/90",
        )}
      >
        {saveStatus === "saving" ? (
          <><Loader2 className="h-4 w-4 animate-spin" /> {t("model.saving")}</>
        ) : saveStatus === "success" ? (
          <><Check className="h-4 w-4" /> {t("model.saved")}</>
        ) : saveStatus === "error" ? (
          <><AlertCircle className="h-4 w-4" /> {t("model.saveError")}</>
        ) : (
          <><Save className="h-4 w-4" /> {t("model.saveConfig")}</>
        )}
      </Button>
    </div>
  );
}

/** Provider + Model dual selector with API key */
function ModelSelector({
  value,
  onChange,
  providerModelsMap,
  providerNames,
  providers,
  onApiKeyChange,
}: {
  value: string;
  onChange: (v: string) => void;
  providerModelsMap: Record<string, string[]>;
  providerNames: string[];
  providers: ProviderConfig[];
  onApiKeyChange: (providerName: string, apiKey: string) => void;
}) {
  const t = useT();
  const [apiKey, setApiKey] = useState("");
  const [showKey, setShowKey] = useState(false);

  // Parse "provider/model" into parts
  const slashIdx = value.indexOf("/");
  const selectedProvider = slashIdx > 0 ? value.substring(0, slashIdx) : "";
  const selectedModel = slashIdx > 0 ? value.substring(slashIdx + 1) : "";

  const modelsForProvider = selectedProvider ? (providerModelsMap[selectedProvider] || []) : [];

  // Load existing API key when provider selection or provider data changes
  useEffect(() => {
    if (selectedProvider) {
      const p = providers.find((prov) => prov.name === selectedProvider);
      setApiKey(p?.apiKey || "");
    } else {
      setApiKey("");
    }
    setShowKey(false);
  }, [selectedProvider, providers]);

  const handleProviderChange = (provider: string) => {
    const models = providerModelsMap[provider] || [];
    if (models.length === 1) {
      onChange(`${provider}/${models[0]}`);
    } else {
      onChange(`${provider}/`);
    }
  };

  const handleModelChange = (model: string) => {
    onChange(`${selectedProvider}/${model}`);
  };

  return (
    <div className="space-y-2">
      <div className="grid grid-cols-2 gap-2">
        <div className="space-y-1.5">
          <Label className="text-xs text-muted-foreground">{t("model.provider")}</Label>
          <Select value={selectedProvider} onValueChange={handleProviderChange}>
            <SelectTrigger>
              <SelectValue placeholder={t("model.selectProvider")} />
            </SelectTrigger>
            <SelectContent>
              {providerNames.map((p) => (
                <SelectItem key={p} value={p}>{p}</SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="space-y-1.5">
          <Label className="text-xs text-muted-foreground">{t("model.modelLabel")}</Label>
          {modelsForProvider.length > 0 ? (
            <Select value={selectedModel} onValueChange={handleModelChange}>
              <SelectTrigger>
                <SelectValue placeholder={t("model.selectModel")} />
              </SelectTrigger>
              <SelectContent>
                {modelsForProvider.map((m) => (
                  <SelectItem key={m} value={m}>{m}</SelectItem>
                ))}
              </SelectContent>
            </Select>
          ) : (
            <Input
              placeholder={t("model.modelId")}
              value={selectedModel}
              onChange={(e) => {
                if (selectedProvider) {
                  onChange(`${selectedProvider}/${e.target.value}`);
                } else {
                  onChange(e.target.value);
                }
              }}
            />
          )}
        </div>
      </div>
      {selectedProvider && (
        <div className="space-y-1.5">
          <Label className="text-xs text-muted-foreground">{t("model.apiKey")}</Label>
          <div className="relative">
            <Input
              type={showKey ? "text" : "password"}
              placeholder="sk-..."
              value={apiKey}
              onChange={(e) => {
                setApiKey(e.target.value);
                onApiKeyChange(selectedProvider, e.target.value);
              }}
              className="pr-9"
            />
            <Button
              type="button"
              variant="ghost"
              size="icon"
              onClick={() => setShowKey(!showKey)}
              className="absolute right-0 top-0 h-full w-9 text-muted-foreground hover:text-foreground"
            >
              {showKey ? <EyeOff className="h-3.5 w-3.5" /> : <Eye className="h-3.5 w-3.5" />}
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}

function SectionHeader({ title, description }: { title: string; description: string }) {
  return (
    <div>
      <h3 className="text-sm font-medium text-foreground">{title}</h3>
      <p className="text-xs text-muted-foreground mt-0.5">{description}</p>
    </div>
  );
}

function ProviderForm({
  provider,
  onSave,
  onCancel,
}: {
  provider: ProviderConfig;
  onSave: (p: ProviderConfig) => void;
  onCancel: () => void;
}) {
  const t = useT();
  const [form, setForm] = useState(provider);
  const [validationError, setValidationError] = useState("");
  // Stable keys for model list items (parallel array, avoids modifying ProviderModel type)
  const [modelKeys, setModelKeys] = useState(() =>
    provider.models.map(() => crypto.randomUUID()),
  );

  const updateField = <K extends keyof ProviderConfig>(
    key: K,
    value: ProviderConfig[K],
  ) => {
    setForm((prev) => ({ ...prev, [key]: value }));
  };

  const addModel = () => {
    updateField("models", [
      ...form.models,
      { name: "", id: "", contextWindow: undefined, maxTokens: undefined, reasoning: undefined },
    ]);
    setModelKeys((prev) => [...prev, crypto.randomUUID()]);
  };

  const updateModel = (index: number, model: ProviderModel) => {
    const newModels = [...form.models];
    newModels[index] = model;
    updateField("models", newModels);
  };

  const removeModel = (index: number) => {
    updateField(
      "models",
      form.models.filter((_, i) => i !== index),
    );
    setModelKeys((prev) => prev.filter((_, i) => i !== index));
  };

  return (
    <>
      <DialogHeader>
        <DialogTitle>
          {provider.name ? t("model.editProvider") : t("model.newProvider")}
        </DialogTitle>
        <DialogDescription>
          {t("model.providerFormDesc")}
        </DialogDescription>
      </DialogHeader>

      <div className="space-y-4 pt-2">
        <FormField label={t("model.providerName")}>
          <Input
            value={form.name}
            onChange={(e) => updateField("name", e.target.value)}
            disabled={!!provider.name}
            placeholder="my-provider"
          />
        </FormField>
        <FormField label={t("model.baseUrl")}>
          <Input
            value={form.baseUrl}
            onChange={(e) => updateField("baseUrl", e.target.value)}
            placeholder="https://api.openai.com/v1"
          />
        </FormField>
        <FormField label={t("model.apiKey")}>
          <Input
            type="password"
            value={form.apiKey}
            onChange={(e) => updateField("apiKey", e.target.value)}
            placeholder="sk-..."
          />
        </FormField>
        <FormField label={t("model.apiType")}>
          <Select
            value={form.api}
            onValueChange={(v) => updateField("api", v)}
          >
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="openai">{t("model.apiOpenai")}</SelectItem>
              <SelectItem value="anthropic">{t("model.apiAnthropic")}</SelectItem>
              <SelectItem value="google">{t("model.apiGoogle")}</SelectItem>
            </SelectContent>
          </Select>
        </FormField>

        <Separator />

        {/* Models */}
        <div>
          <Label className="text-xs font-medium">{t("settings.models")}</Label>
          <div className="mt-2 space-y-2">
            {form.models.map((m, i) => (
              <div key={modelKeys[i]} className="flex gap-2 group">
                <Input
                  className="flex-1 h-8 text-xs"
                  placeholder={t("model.modelName")}
                  value={m.name}
                  onChange={(e) => updateModel(i, { ...m, name: e.target.value })}
                />
                <Input
                  className="flex-1 h-8 text-xs"
                  placeholder={t("model.modelId")}
                  value={m.id}
                  onChange={(e) => updateModel(i, { ...m, id: e.target.value })}
                />
                <Button
                  variant="ghost"
                  size="icon"
                  onClick={() => removeModel(i)}
                  className="h-8 w-8 text-muted-foreground hover:text-destructive opacity-0 group-hover:opacity-100 transition-opacity"
                >
                  <X className="h-3.5 w-3.5" />
                </Button>
              </div>
            ))}
            <Button
              variant="ghost"
              size="sm"
              onClick={addModel}
              className="text-xs text-muted-foreground hover:text-primary gap-1.5 px-2 h-8"
            >
              <Plus className="h-3.5 w-3.5" />
              {t("model.addModel")}
            </Button>
          </div>
        </div>

        {validationError && (
          <p className="text-xs text-destructive">{validationError}</p>
        )}

        <div className="flex gap-2 pt-2">
          <Button onClick={() => {
            if (!form.name.trim()) {
              setValidationError(t("model.nameRequired"));
              return;
            }
            if (!form.baseUrl.trim()) {
              setValidationError(t("model.urlRequired"));
              return;
            }
            setValidationError("");
            onSave(form);
          }} className="flex-1 gap-2">
            <Save className="h-4 w-4" />
            {t("model.saveProvider")}
          </Button>
          <Button variant="outline" onClick={onCancel}>
            {t("model.cancel")}
          </Button>
        </div>
      </div>
    </>
  );
}

function FormField({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="space-y-1.5">
      <Label className="text-xs text-muted-foreground">{label}</Label>
      {children}
    </div>
  );
}
