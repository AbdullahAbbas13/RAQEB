import { Injectable, OnDestroy } from "@angular/core";
import { Subject, BehaviorSubject, fromEvent } from "rxjs";
import { takeUntil, debounceTime } from "rxjs/operators";
import { Router } from "@angular/router";

// Menu
export interface Menu {
  headTitle1?: string;
  headTitle2?: string;
  path?: string;
  title?: string;
  icon?: string;
  type?: string;
  badgeType?: string;
  badgeValue?: string;
  active?: boolean;
  bookmark?: boolean;
  children?: Menu[];
}

@Injectable({
  providedIn: "root",
})
export class NavService implements OnDestroy {
  private unsubscriber: Subject<any> = new Subject();
  public screenWidth: BehaviorSubject<number> = new BehaviorSubject(window.innerWidth);

  // Search Box
  public search: boolean = false;

  // Language
  public language: boolean = false;

  // Mega Menu
  public megaMenu: boolean = false;
  public levelMenu: boolean = false;
  public megaMenuColapse: boolean = window.innerWidth < 1199 ? true : false;

  // Collapse Sidebar
  public collapseSidebar: boolean = window.innerWidth < 991 ? true : false;

  // For Horizontal Layout Mobile
  public horizontal: boolean = window.innerWidth < 991 ? false : true;

  // Full screen
  public fullScreen: boolean = false;

  constructor(private router: Router) {
    this.setScreenWidth(window.innerWidth);
    fromEvent(window, "resize")
      .pipe(debounceTime(1000), takeUntil(this.unsubscriber))
      .subscribe((evt: any) => {
        this.setScreenWidth(evt.target.innerWidth);
        if (evt.target.innerWidth < 991) {
          this.collapseSidebar = true;
          this.megaMenu = false;
          this.levelMenu = false;
        }
        if (evt.target.innerWidth < 1199) {
          this.megaMenuColapse = true;
        }
      });
    if (window.innerWidth < 991) {
      // Detect Route change sidebar close
      this.router.events.subscribe((event) => {
        this.collapseSidebar = true;
        this.megaMenu = false;
        this.levelMenu = false;
      });
    }
  }

  ngOnDestroy() {
    // this.unsubscriber.next();
    this.unsubscriber.complete();
  }

  private setScreenWidth(width: number): void {
    this.screenWidth.next(width);
  }

  MENUITEMS: Menu[] = [
    {
      headTitle1: "GENERAL",
      headTitle2: "ReadyToUseApps",
    },
    {
      title: "LGD",
      icon: "widget",
      type: "sub",
      badgeType: "light-secondary",
      // badgeValue: "New",
      active: true,
      children: [
        { path: "/LGD/list", title: "LGDList", type: "link" },
         { path: "/LGD/form", title: "CreateLGD", type: "link" },
      ],
    },
    {
      title: "PD",
      icon: "task",
      type: "sub",
      badgeType: "light-secondary",
      // badgeValue: "New",
      active: true,
      children: [
        { path: "/PD/list", title: "PDList", type: "link" },
        { path: "/PD/form", title: "CreatePD", type: "link" },
      ],
    },
    // {
    //   title: "MyActions",
    //   icon: "task",
    //   type: "sub",
    //   badgeType: "light-primary",
    //   // badgeValue: "2",
    //   active: false,
    //   children: [
    //     { path: "/action-tracking", title: "MyActions", type: "link" },
    //     // { path: "/simple-page/second-page", title: "SecondPage", type: "link" },
    //   ],
    // },
    // { path: "/site-risk-profile-summery", icon: "to-do", title: "SiteRiskSummery", active: false, type: "link", bookmark: true },
    // {
    //   title: "UserInformation",
    //   icon: "user",
    //   type: "sub",
    //   badgeType: "light-primary",
    //   active: false,
    //   children: [
    //     { path: "/simple-page/first-page1", title: "AddNewUser", type: "link" },
    //     { path: "/simple-page/first-page2", title: "ListofUsers", type: "link" },
    //     { path: "/simple-page/first-page3", title: "AddNewGroup", type: "link" },
    //     { path: "/simple-page/second-page4", title: "ListofGroup", type: "link" },
    //   ],
    // },
    // {
    //   title: "SiteInformation",
    //   icon: "home",
    //   type: "sub",
    //   badgeType: "light-primary",
    //   active: false,
    //   children: [
    //     { path: "/simple-page/first-page5", title: "AddNewSite", type: "link" },
    //     { path: "/simple-page/first-page6", title: "ListofSites", type: "link" },
    //   ],
    // },
    // {
    //   title: "Security",
    //   icon: "home",
    //   type: "sub",
    //   badgeType: "light-primary",
    //   active: false,
    //   children: [
    //     { path: "/simple-page/first-page9", title: "ControlPermissions", type: "link" },
    //   ],
    // },
    {
      title: "Settings",
      icon: "home",
      type: "sub",
      badgeType: "light-primary",
      active: false,
      children: [
        // { path: "/simple-page/first-page10", title: "EmailConfigurations", type: "link" },
        { path: "/customer", title: "Customers", type: "link" },
        { path: "/user", title: "Users", type: "link" },
        { path: "/segment", title: "Segments", type: "link" },
        { path: "/language", title: "Languages", type: "link" },
        { path: "/localization", title: "Localizations", type: "link" },
        { path: "/localization-language", title: "LocalizationLanguage", type: "link" },
      ],
    },
    // {
    //   title: "Admin",
    //   icon: "home",
    //   type: "sub",
    //   badgeType: "light-primary",
    //   active: false,
    //   children: [
    //     { path: "/simple-page/first-page11", title: "ListofFeedback", type: "link" },
    //   ],
    // },
    // {
    //   title: "Support",
    //   icon: "home",
    //   type: "sub",
    //   badgeType: "light-primary",
    //   active: false,
    //   children: [
    //     { path: "/simple-page/first-page12", title: "QuestionandAnswers ", type: "link" },
    //     { path: "/simple-page/first-page13", title: "SendFeedback", type: "link" },
    //   ],
    // },
  ];

  // Array
  items = new BehaviorSubject<Menu[]>(this.MENUITEMS);
}
