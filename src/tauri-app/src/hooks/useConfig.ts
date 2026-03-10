import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import {
  configRead,
  configSaveModels,
  configSaveProvider,
  configDeleteProvider,
  configSaveProviderApiKey,
  configSaveChannel,
  configListModels,
  configInstallDingtalk,
} from "../lib/tauri-commands";
import type { ModelConfig, ProviderConfig, ChannelConfig } from "../lib/types";

export function useConfig() {
  return useQuery({
    queryKey: ["config"],
    queryFn: configRead,
    staleTime: 30_000,
  });
}

export function useAvailableModels() {
  return useQuery({
    queryKey: ["models"],
    queryFn: configListModels,
    staleTime: 60_000,
  });
}

export function useSaveModelConfig() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (config: ModelConfig) => configSaveModels(config),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["config"] }),
  });
}

export function useSaveProvider() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (provider: ProviderConfig) => configSaveProvider(provider),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["config"] }),
  });
}

export function useDeleteProvider() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => configDeleteProvider(name),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["config"] }),
  });
}

export function useSaveProviderApiKey() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ providerName, apiKey }: { providerName: string; apiKey: string }) =>
      configSaveProviderApiKey(providerName, apiKey),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["config"] }),
  });
}

export function useSaveChannel() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (channel: ChannelConfig) => configSaveChannel(channel),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["config"] }),
  });
}

export function useInstallDingtalk() {
  return useMutation({
    mutationFn: configInstallDingtalk,
  });
}
