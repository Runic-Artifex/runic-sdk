import type { Metadata } from "next";
import Link from "next/link";
import { products } from "../docs-data";

export const metadata: Metadata = { title: "Products", description: "Choose the independent Runic Artifex product that owns the capability you need." };

export default function ProductsPage() {
  return <main><section className="page-hero shell"><p className="eyebrow">Product map</p><h1>Independent products. Designed to compose.</h1><p className="lede">Start at the narrowest boundary that solves your problem. Add Runic Toolkit only when you need the shared application-composition experience.</p></section><section className="shell product-grid section">{products.map((product) => <Link className="product-card" href={`/products/${product.slug}`} key={product.slug}><span className="product-mark" aria-hidden="true">{product.mark}</span><p className="kicker">{product.kicker}</p><h3>{product.name}</h3><p>{product.summary}</p><span className="card-link">{product.kind === "application" ? "Editor and boundaries" : "Packages and boundaries"} →</span></Link>)}</section></main>;
}
