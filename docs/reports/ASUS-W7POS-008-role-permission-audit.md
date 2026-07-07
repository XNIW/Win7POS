# ASUS-W7POS-008 Role Permission Audit

Date: 2026-07-07
Base HEAD at audit start: `c91d17a`

## Summary

The observed `manager` denial for Users/Roles and Database maintenance is consistent with the current Win7POS local role policy.

`UserRepository.MapRemoteRoleKey` maps Admin Console roles this way:

| Remote role key | Local role |
| --- | --- |
| `admin` | `admin` |
| `shop_owner` | `admin` |
| `manager` | `manager` |
| `shop_manager` | `manager` |
| `supervisor` | `supervisor` |
| `cashier` | `cashier` |
| unknown/blank | `cashier` |

Current hierarchy is therefore:

`admin/shop_owner > manager/shop_manager > supervisor > cashier`

No policy change was made in this task. In particular, `manager` and `shop_manager` were not promoted to admin.

## Seeded Role Matrix

| Role | Intended level | Current permission codes | Sensitive access yes/no |
| --- | --- | --- | --- |
| `admin` | Admin / shop owner | All codes: `pos.sell`, `pos.pay`, `pos.suspend_cart`, `pos.recover_cart`, `pos.discount`, `pos.discount_over_limit`, `pos.refund`, `pos.void_sale`, `pos.reprint_receipt`, `catalog.view`, `catalog.edit`, `catalog.import`, `catalog.price_edit`, `register.view`, `register.view_all`, `daily_close.view`, `daily_close.run`, `daily_close.print`, `settings.shop`, `settings.printer`, `db.backup`, `db.restore`, `db.maintenance`, `users.manage`, `roles.manage`, `security.override` | Yes: Users/Roles, DB maintenance, DB restore, products view/edit/import/prices, daily close, shop/printer settings, sales register all, security override. |
| `manager` | Store manager / shop manager | `pos.sell`, `pos.pay`, `pos.suspend_cart`, `pos.recover_cart`, `pos.discount`, `pos.discount_over_limit`, `pos.refund`, `pos.void_sale`, `pos.reprint_receipt`, `catalog.view`, `catalog.edit`, `catalog.price_edit`, `register.view`, `register.view_all`, `daily_close.view`, `daily_close.run`, `daily_close.print`, `settings.shop`, `settings.printer`, `db.backup` | Yes: products view/edit/price edit, daily close, shop/printer settings, sales register all, DB backup if exposed. No: Users/Roles, DB maintenance, DB restore, catalog import, roles/security override. |
| `supervisor` | Shift supervisor | `pos.sell`, `pos.pay`, `pos.suspend_cart`, `pos.recover_cart`, `pos.discount`, `pos.refund`, `pos.void_sale`, `pos.reprint_receipt`, `catalog.view`, `register.view`, `register.view_all`, `daily_close.view`, `daily_close.run`, `daily_close.print`, `settings.printer` | Yes: POS supervisor actions, products view, daily close, printer settings, sales register all. No: Users/Roles, DB maintenance/restore/backup, shop settings, product edit/import/price edit, security override. |
| `cashier` | Frontline cashier | `pos.sell`, `pos.pay`, `pos.suspend_cart`, `pos.recover_cart`, `pos.reprint_receipt`, `catalog.view`, `register.view` | Yes: POS sales, suspend/recover cart, receipt reprint, products view, own/register view. No: Users/Roles, DB maintenance, daily close, settings, product edit/import/price edit, refunds/voids/discounts. |

## Gate Audit

| Area/action | Current gate | Notes |
| --- | --- | --- |
| Users/Roles menu | `PermissionCodes.UsersManage` in `MainWindow`; `PosViewModel.CreateUserManagementViewModel` demands `users.manage`; role editing depends on `roles.manage`. | `manager` is denied by current seed. Denial now shows current role and missing permission, and logs `category=permission.denied`. |
| Database maintenance menu | `PermissionCodes.DbMaintenance` in `MainWindow`; `PosViewModel.OpenDbMaintenanceAsync` also demands `db.maintenance`; restore sub-action uses `db.restore` or override flow. | `manager` is denied by current seed. This is expected unless Admin Console should mirror an `admin`/`shop_owner`. |
| Products menu | `PermissionCodes.CatalogView` in `MainWindow`. | `manager`, `supervisor`, and `cashier` can open products because all have `catalog.view`. |
| Product edit/new/delete/copy | `catalog.edit` inside `ProductsViewModel`. | `manager` can edit; supervisor/cashier cannot. |
| Product import / supplier Excel import | `catalog.import` inside `ProductsViewModel`. | Only `admin` has this by seed. `manager` is denied by current seed. |
| Price history | Open requires selected product; edit ability is controlled by `catalog.price_edit`. | `manager` can edit prices; supervisor/cashier can view but not edit. |
| Product export | No dedicated permission check in `ProductsViewModel.ExportDataAsync`. | Current behavior allows any operator who can open Products (`catalog.view`) to export. Consider a follow-up policy decision if export should require a stricter permission. |
| Daily close menu | `daily_close.view` in `MainWindow`; run/print actions are represented by `daily_close.run` and `daily_close.print`. | `manager` and `supervisor` can access by seed. |
| Printer settings | `settings.printer` in `PosViewModel.OpenPrinterSettingsAsync`. | `manager` and `supervisor` can access; cashier cannot unless override succeeds. |
| Official shop data | `settings.shop` in `PosViewModel.OpenShopSettingsAsync`. | `manager` can access; supervisor/cashier cannot unless override succeeds. |
| Sales register | `register.view` in `PosViewModel.OpenSalesRegisterInternalAsync`; `register.view_all` controls viewing all operators. | manager/supervisor can view all; cashier has register view only. |

## Diagnostic Changes Added

When a MainWindow-gated menu action is denied, the user-facing prompt now includes:

```text
Permission denied. Current role: manager. Missing permission: UsersManage.
You do not have permission to access users and roles.
```

The prompt keeps the existing `OK` and `Switch operator` choices. If the operator switches successfully and the new operator has the missing permission, the original action is retried.

If the quick switch is opened for a missing permission and no local operator has that permission, the switch dialog now explains:

```text
No local operator with UsersManage is available. Use POS access with an admin/shop_owner online at least once.
```

This matches the current Admin Console/POS mirror model: an admin/shop_owner must be mirrored locally before local elevation can succeed offline or from quick switch.

## Logging

New structured examples:

```text
category=permission.denied currentRole=manager missingPermission=UsersManage action=UsersRoles stage=initial
category=permission.denied currentRole=manager missingPermission=DbMaintenance action=DatabaseMaintenance stage=initial
```

The log line intentionally includes only role, permission, action, and stage. It does not include PIN, password, credential, token, staff credential, or bearer/auth data.

## Decision Point For Follow-Up

If business policy says a `manager` should manage users/roles or database maintenance, the follow-up should be explicit:

- change Admin Console to send `shop_owner`/`admin` for those operators; or
- change Win7POS local role seeds/mapping so `manager` receives selected extra permissions.

This task did not make that policy change.
