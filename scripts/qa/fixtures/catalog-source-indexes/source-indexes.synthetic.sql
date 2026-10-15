create index inventory_products_sync_event_id_text_idx
  on public.inventory_products ((lower(id::text)));
create index inventory_product_prices_sync_event_id_text_idx
  on public.inventory_product_prices ((lower(id::text)));
