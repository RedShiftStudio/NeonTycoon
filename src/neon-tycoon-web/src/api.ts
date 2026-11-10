export type Player={id:number;name:string;level:number;experience:number;nextLevel:number;prestige:number;credits:number;energy:number;energyCap:number;perSecond:number;tapReward:number;lifetimeCredits:number;referralCode:string;serverTime:string};
export type Building={id:string;name:string;icon:string;district:number;requiredLevel:number;production:number;baseCost:number;count:number;level:number;purchaseCost:number;upgradeCost:number};
export type Research={id:string;name:string;description:string;cost:number;requiredLevel:number;prerequisite:string|null;unlocked:boolean};
export type Objective={id:string;name:string;target:number;reward:number;progress:number;claimed:boolean;period?:string};
export type Daily={dailyStreak:number;available:boolean;nextReward:number};
export type Event={name:string;startsAt:string;endsAt:string;multiplier:number};
let token:string|null=null;
export const setToken=(value:string)=>{token=value};
export async function api<T>(path:string,method='GET',body?:unknown):Promise<T>{const response=await fetch('/api'+path,{method,headers:{...(token?{Authorization:`Bearer ${token}`} : {}),...(body?{'Content-Type':'application/json'}:{}),...(method==='POST'&&token?{'Idempotency-Key':crypto.randomUUID()}:{})},body:body?JSON.stringify(body):undefined,signal:AbortSignal.timeout(15000)});if(!response.ok){const error=await response.json().catch(()=>({error:'Connection lost'})) as {error?:string};throw new Error(error.error||'Request failed')}return response.json() as Promise<T>}
export const format=(value:number)=>Intl.NumberFormat('en',{notation:value>=10000?'compact':'standard',maximumFractionDigits:value>=1000?1:2}).format(value);
