import { error } from '@sveltejs/kit';
import { getProduct } from '$lib/docs-data';
import type { PageLoad } from './$types';

export const load: PageLoad = ({ params }) => {
  const product = getProduct(params.slug);
  if (!product) error(404, 'Product not found');
  return { product };
};
