
import { IDataProvider, NormalizedLiveMatch, NormalizedMatch, NormalizedMatchDetails } from './types.js';

export class EmptyFootballProvider implements IDataProvider {
  public readonly name = 'empty';
  
  async getLiveMatches(): Promise<NormalizedLiveMatch[]> { return []; }
  async getDailyMatches(date: string): Promise<NormalizedMatch[]> { return []; }
  async getMatchDetails(id: string): Promise<NormalizedMatchDetails> { throw new Error('Not implemented'); }
}
