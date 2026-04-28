import type { HTMLAttributes } from "react";
import { cn } from "@/shared/utils/cn";

type Variant = "default" | "destructive" | "success";

const variants: Record<Variant, string> = {
  default: "border-border bg-muted text-foreground",
  destructive: "border-destructive/40 bg-destructive/10 text-destructive",
  success: "border-emerald-300 bg-emerald-50 text-emerald-900",
};

export function Alert({
  className,
  variant = "default",
  ...props
}: HTMLAttributes<HTMLDivElement> & { variant?: Variant }) {
  return (
    <div
      role="alert"
      className={cn("rounded-md border p-4 text-sm", variants[variant], className)}
      {...props}
    />
  );
}
