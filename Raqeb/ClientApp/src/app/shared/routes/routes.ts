import { Routes } from "@angular/router";

export const content: Routes = [
  {
    path: "auth",
    loadChildren: () => import("../../auth/auth.module").then((m) => m.AuthModule),
  },
  {
    path: "LGD",
    loadChildren: () => import("../../components/simple-page/simple-page.module").then((m) => m.SimplePageModule),
  },
  {
    path: "PD",
    loadChildren: () => import("../../components/pd/pd.module").then((m) => m.PDModule),
  },
  {
    path: "customer",
    loadChildren: () => import("../../components/dashboard/customer/customer.module").then((m) => m.CustomerModule),
  },
  {
    path: "user",
    loadChildren: () => import("../../components/dashboard/user/user.module").then((m) => m.UserModule),
  },
  {
    path: "language",
    loadChildren: () => import("../../components/dashboard/language/language.module").then((m) => m.LanguageModule),
  },
  {
    path: "localization",
    loadChildren: () => import("../../components/dashboard/localization/localization.module").then((m) => m.LocalizationModule),
  },
  {
    path: "localization-language",
    loadChildren: () => import("../../components/dashboard/localization-language/localization-lang.module").then((m) => m.LocalizationLangModule),
  },
];
